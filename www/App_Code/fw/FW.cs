﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Net.Mail;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace osafw
{
    // standard exceptions used by framework 
    [Serializable]
    public class AuthException : ApplicationException
    {
        public AuthException(string message) : base(message) { }
    }
    [Serializable]
    public class UserException : ApplicationException
    {
        public UserException(string message) : base(message) { }
    }
    [Serializable]
    public class ValidationException : ApplicationException { }
    [Serializable]
    public class RedirectException : Exception { }

    /// <summary>
    /// Logger levels, ex: logger(LogLevel.ERROR, "Something happened")
    /// </summary>
    public enum LogLevel : int
    {
        OFF,             // no logging occurs
        FATAL,           // severe error, current request (or even whole application) aborted (notify admin)
        ERROR,           // error happened, but current request might still continue (notify admin)
        WARN,            // potentially harmful situations for further investigation, request processing continues
        INFO,            // default for production (easier maintenance/support), progress of the application at coarse-grained level (fw request processing: request start/end, sql, route/external redirects, sql, fileaccess, third-party API)
        DEBUG,           // default for development (default for logger("msg") call), fine-grained level
        TRACE,           // very detailed dumps (in-module details like fw core, despatcher, parse page, ...)
        ALL              // just log everything
    }

    public class FwRoute
    {
        public string controller_path; // store /Prefix/Controller - to use in parser a default path for templates
        public string method;
        public string controller;
        public string action;
        public string action_raw;
        public string id;
        public string action_more;
        public string format;
        public ArrayList @params;
    }

    public class FW : IDisposable
    {
        public const string FW_NAMESPACE_PREFIX = "osafw.";
        public static Hashtable METHOD_ALLOWED = Utils.qh("GET POST PUT DELETE");


        private System.IO.FileStream floggerFS;
        private System.IO.StreamWriter floggerSW;

        private readonly Hashtable models = new Hashtable();
        public static FW Current; // store FW current "singleton", set in run WARNING - avoid to use as if 2 parallel requests come, a bit later one will overwrite this
        public FwCache cache = new FwCache(); // request level cache

        public Hashtable FORM;
        public Hashtable G; // for storing global vars - used in template engine, also stores "_flash"
        public Hashtable FERR; // for storing form id's with error messages, put to hf("ERR") for parser

        public DB db;

        public HttpContext context;
        public HttpRequest req;
        public HttpResponse resp;

        public string request_url; // current request url (relative to application url)
        public FwRoute route = new FwRoute();
        public TimeSpan request_time; // after dispatch() - total request processing time

        public string cache_control = "no-cache"; // cache control header to add to pages, controllers can change per request
        public bool is_log_events = true; // can be set temporarly to false to prevent event logging (for batch process for ex)

        public string last_error_send_email = "";

        //TODO MIGRATE #Const isSentry = False 'if you use Sentry set to True here, install SentrySDK, in web.config fill endpoint URL to "log_sentry" 
        private IDisposable sentryClient;

        // begin processing one request
        public static void run(HttpContext context, IConfiguration configuration)
        {
            FW fw = new FW(context, configuration);
            FW.Current = fw;

            FwHooks.initRequest(fw);
            fw.dispatch();
            FwHooks.finalizeRequest(fw);
        }

        public FW(HttpContext context, IConfiguration configuration)
        {
            this.context = context;
            this.req = context.Request;
            this.resp = context.Response;

            FwConfig.init(context, configuration);

            logger("NEW FW INSTANCE - TODO MIGRATE test it's once per request, test parallel requests **************");

            //TODO MIGRATE
            //# If isSentry Then
            //            'Sentry Raven processing
            //        sentryClient = Sentry.SentrySdk.Init(config("log_sentry"))
            //        Sentry.SentrySdk.ConfigureScope(Sub(scope) scope.User = New Sentry.Protocol.User With {.Email = SESSION("login")})
            //#End If

            db = new DB(this);
            DB.SQL_QUERY_CTR = 0; // reset query counter

            G = (Hashtable)config().Clone(); // by default G contains conf

            // per request settings
            G["request_url"] = UriHelper.GetDisplayUrl(req);

            // override default lang with user's lang
            if (!string.IsNullOrEmpty(Session("lang"))) G["lang"] = Session("lang");

            FERR = new Hashtable(); // reset errors
            parseForm();

            // save flash to current var and update session as flash is used only for nearest request
            Hashtable _flash = SessionHashtable("_flash");
            if (_flash != null) G["_flash"] = _flash;
            SessionHashtable("_flash", new Hashtable());
        }

        // ***************** work with SESSION
        //by default Session is for strings
        public string Session(string name)
        {
            return context.Session.GetString(name);
        }
        public void Session(string name, string value)
        {
            context.Session.SetString(name, value);
        }

        public int? SessionInt(string name)
        {
            return context.Session.GetInt32(name);
        }
        public void SessionInt(string name, int value)
        {
            context.Session.SetInt32(name, value);
        }


        public bool SessionBool(string name)
        {
            var data = context.Session.Get(name);
            if (data == null)
            {
                return false;
            }
            return BitConverter.ToBoolean(data, 0);
        }
        public void SessionBool(string name, bool value)
        {
            context.Session.Set(name, BitConverter.GetBytes(value));
        }

        public Hashtable SessionHashtable(string name)
        {
            string data = context.Session.GetString(name);
            return data == null ? null : (Hashtable)Utils.deserialize(data);
        }
        public void SessionHashtable(string name, Hashtable value)
        {
            context.Session.SetString(name, Utils.serialize(value));
        }


        // FLASH - used to pass something to the next request (and only on this request)        
        // get flash value by name
        // set flash value by name - return fw in this case
        public object flash(string name, object value = null)
        {
            if (value == null)
            {
                // read mode - return current flash
                return ((Hashtable)this.G["_flash"])[name];
            }
            else
            {
                // write for the next request
                Hashtable _flash = SessionHashtable("_flash") ?? new();
                _flash[name] = value;
                SessionHashtable("_flash", _flash);
                return this; // for chaining
            }
        }

        // return all the settings
        public Hashtable config()
        {
            return FwConfig.settings;
        }
        // return just particular setting
        public object config(string name)
        {
            return FwConfig.settings[name];
        }

        /// <summary>
        /// returns format expected by client browser
        /// </summary>
        /// <returns>"pjax", "json" or empty (usual html page)</returns>
        public string getResponseExpectedFormat()
        {
            string result = "";
            if (this.route.format == "json" || ((string)this.req.Headers["Accept"]).Contains("application/json"))
                result = "json";
            else if (this.route.format == "pjax" || !string.IsNullOrEmpty(this.req.Headers["X-Requested-With"]))
                result = "pjax";
            return result;
        }

        /// <summary>
        /// return true if browser requests json response
        /// </summary>
        /// <returns></returns>
        public bool isJsonExpected()
        {
            return getResponseExpectedFormat() == "json";
        }

        public void getRoute()
        {
            string url = req.Path;
            //TODO MIGRATE test
            // cut the App path from the begin
            if (req.PathBase.Value.Length > 1) url = url.Replace(req.PathBase, "");
            url = Regex.Replace(url, @"\/$", ""); // cut last / if any
            this.request_url = url;

            logger(LogLevel.TRACE, "REQUESTING ", url);

            // init defaults
            route = new FwRoute()
            {
                controller = "Home",
                action = "Index",
                action_raw = "",
                id = "",
                action_more = "",
                format = "html",
                method = req.Method,
                @params = new ArrayList()
            };

            // check if method override exits
            if (FORM.ContainsKey("_method"))
            {
                if (METHOD_ALLOWED.ContainsKey(FORM["_method"]))
                    route.method = (string)FORM["_method"];
            }
            if (route.method == "HEAD") route.method = "GET"; // for website processing HEAD is same as GET, IIS will send just headers

            string controller_prefix = "";

            // process config special routes (redirects, rewrites)
            Hashtable routes = (Hashtable)this.config("routes");
            bool is_routes_found = false;
            if (routes != null)
            {
                foreach (string route_key in routes.Keys)
                {
                    if (url == route_key)
                    {
                        string rdest = (string)routes[route_key];
                        Match m1 = Regex.Match(rdest, "^(?:(GET|POST|PUT|DELETE) )?(.+)");
                        if (m1.Success)
                        {
                            // override method
                            if (!string.IsNullOrEmpty(m1.Groups[1].Value)) route.method = m1.Groups[1].Value;
                            if (m1.Groups[2].Value.Substring(0, 1) == "/")
                            {
                                // if started from / - this is redirect url
                                url = m1.Groups[2].Value;
                            }
                            else
                            {
                                // it's a direct class-method to call, no further REST processing required
                                is_routes_found = true;
                                string[] sroute = m1.Groups[2].Value.Split("::", 2);
                                route.controller = Utils.routeFixChars(sroute[0]);
                                if (sroute.GetUpperBound(1) > 0)
                                    route.action_raw = sroute[1];
                                break;
                            }
                        }
                        else
                            logger(LogLevel.WARN, "Wrong route destination: " + rdest);
                    }
                }
            }

            if (!is_routes_found)
            {
                // TODO move prefix cut to separate func
                string prefix_rx = FwConfig.getRoutePrefixesRX();
                route.controller_path = "";
                Match m_prefix = Regex.Match(url, prefix_rx);
                if (m_prefix.Success)
                {
                    // convert from /Some/Prefix to SomePrefix
                    controller_prefix = Utils.routeFixChars(m_prefix.Groups[1].Value);
                    route.controller_path = "/" + controller_prefix;
                    url = m_prefix.Groups[2].Value;
                }

                // detect REST urls
                // GET   /controller[/.format]       Index
                // POST  /controller                 Save     (save new record - Create)
                // PUT   /controller                 SaveMulti (update multiple records)
                // GET   /controller/new             ShowForm (show new form - ShowNew)
                // GET   /controller/{id}[.format]   Show     (show in format - not for editing)
                // GET   /controller/{id}/edit       ShowForm (show edit form - ShowEdit)
                // GET   /controller/{id}/delete     ShowDelete
                // POST/PUT  /controller/{id}        Save     (save changes to exisitng record - Update    Note:Request.Form should contain data
                // POST/DELETE  /controller/{id}            Delete    Note:Request.Form should NOT contain any data
                // 
                // /controller/(Action)              Action    call for arbitrary action from the controller
                Match m = Regex.Match(url, @"^/([^/]+)(?:/(new|\.\w+)|/([\d\w_-]+)(?:\.(\w+))?(?:/(edit|delete))?)?/?$");
                if (m.Success)
                {
                    route.controller = Utils.routeFixChars(m.Groups[1].Value);
                    if (string.IsNullOrEmpty(route.controller))
                        throw new Exception("Wrong request");

                    // capitalize first letter - TODO - URL-case-insensitivity should be an option!
                    route.controller = route.controller.Substring(0, 1).ToUpper() + route.controller.Substring(1);
                    route.id = m.Groups[3].Value;
                    route.format = m.Groups[4].Value;
                    route.action_more = m.Groups[5].Value;
                    if (!string.IsNullOrEmpty(m.Groups[2].Value))
                    {
                        if (m.Groups[2].Value == "new")
                            route.action_more = "new";
                        else
                            route.format = m.Groups[2].Value.Substring(1);
                    }

                    // match to method (GET/POST)
                    if (route.method == "GET")
                    {
                        if (route.action_more == "new")
                            route.action_raw = "ShowForm";
                        else if (!string.IsNullOrEmpty(route.id) & route.action_more == "edit")
                            route.action_raw = "ShowForm";
                        else if (!string.IsNullOrEmpty(route.id) & route.action_more == "delete")
                            route.action_raw = "ShowDelete";
                        else if (!string.IsNullOrEmpty(route.id))
                            route.action_raw = "Show";
                        else
                            route.action_raw = "Index";
                    }
                    else if (route.method == "POST")
                    {
                        if (!string.IsNullOrEmpty(route.id))
                        {
                            if (req.Form.Count > 0 || req.Body.Length > 0)
                                route.action_raw = "Save";
                            else
                                route.action_raw = "Delete";
                        }
                        else
                            route.action_raw = "Save";
                    }
                    else if (route.method == "PUT")
                    {
                        if (!string.IsNullOrEmpty(route.id))
                            route.action_raw = "Save";
                        else
                            route.action_raw = "SaveMulti";
                    }
                    else if (route.method == "DELETE" & !string.IsNullOrEmpty(route.id))
                        route.action_raw = "Delete";
                    else
                    {
                        logger(LogLevel.WARN, "Wrong Route Params");
                        logger(LogLevel.WARN, route.method);
                        logger(LogLevel.WARN, url);
                        errMsg("Wrong Route Params");
                        return;
                    }

                    logger(LogLevel.TRACE, "REST controller.action=", route.controller, ".", route.action_raw);
                }
                else
                {
                    // otherwise detect controller/action/id.format/more_action
                    string[] parts = url.Split("/");
                    // logger(parts)
                    int ub = parts.Length - 1;
                    if (ub >= 1)
                        route.controller = Utils.routeFixChars(parts[1]);
                    if (ub >= 2)
                        route.action_raw = parts[2];
                    if (ub >= 3)
                        route.id = parts[3];
                    if (ub >= 4)
                        route.action_more = parts[4];
                }
            }

            route.controller_path = route.controller_path + "/" + route.controller;
            // add controller prefix if any
            route.controller = controller_prefix + route.controller;
            route.action = Utils.routeFixChars(route.action_raw);
            if (string.IsNullOrEmpty(route.action))
                route.action = "Index";
        }

        public void dispatch()
        {
            DateTime start_time = DateTime.Now;

            this.getRoute();

            string[] args = new[] { route.id }; // TODO - add rest of possible params from parts

            try
            {
                var auth_check_controller = _auth(route.controller, route.action);

                Type calledType = Type.GetType(FW_NAMESPACE_PREFIX + route.controller + "Controller", false, true); // case ignored
                if (calledType == null)
                {
                    logger(LogLevel.DEBUG, "No controller found for controller=[", route.controller, "], using default Home");
                    // no controller found - call default controller with default action
                    calledType = Type.GetType(FW_NAMESPACE_PREFIX + "HomeController", true);
                    route.controller_path = "/Home";
                    route.controller = "Home";
                    route.action = "NotFound";
                }
                else
                    // controller found
                    if (auth_check_controller == 1)
                {
                    // but need's check access level on controller level
                    var field = calledType.GetField("access_level", BindingFlags.Public | BindingFlags.Static);
                    if (field != null)
                    {
                        int current_level = Utils.f2int(Session("access_level")); //will be 0 for visitors
                        if (current_level < Utils.f2int(field.GetValue(null)))
                            throw new AuthException("Bad access - Not authorized (2)");
                    }
                }

                logger(LogLevel.TRACE, "TRY controller.action=", route.controller, ".", route.action);

                MethodInfo mInfo = calledType.GetMethod(route.action + "Action");
                if (mInfo == null)
                {
                    logger(LogLevel.DEBUG, "No method found for controller.action=[", route.controller, ".", route.action, "], checking route_default_action");
                    // no method found - try to get default action
                    FieldInfo pInfo = calledType.GetField("route_default_action");
                    if (pInfo != null)
                    {
                        string pvalue = (string)pInfo.GetValue(null);
                        if (pvalue == "index")
                        {
                            // = index - use IndexAction for unknown actions
                            route.action = "Index";
                            mInfo = calledType.GetMethod(route.action + "Action");
                        }
                        else if (pvalue == "show")
                        {
                            // = show - assume action is id and use ShowAction
                            if (!string.IsNullOrEmpty(route.id))
                                route.@params.Add(route.id); // route.id is a first param in this case. TODO - add all rest of params from split("/") here
                            if (!string.IsNullOrEmpty(route.action_more))
                                route.@params.Add(route.action_more); // route.action_more is a second param in this case

                            route.id = route.action_raw;
                            args[0] = route.id;

                            route.action = "Show";
                            mInfo = calledType.GetMethod(route.action + "Action");
                        }
                    }
                }

                // save to globals so it can be used in templates
                G["controller"] = route.controller;
                G["action"] = route.action;
                G["controller.action"] = route.controller + "." + route.action;

                logger(LogLevel.TRACE, "FINAL controller.action=", route.controller, ".", route.action);
                // logger(LogLevel.TRACE, "route.method=" , route.method)
                // logger(LogLevel.TRACE, "route.controller=" , route.controller)
                // logger(LogLevel.TRACE, "route.action=" , route.action)
                // logger(LogLevel.TRACE, "route.format=" , route.format)
                // logger(LogLevel.TRACE, "route.id=" , route.id)
                // logger(LogLevel.TRACE, "route.action_more=" , route.action_more)

                logger(LogLevel.INFO, "REQUEST START [", route.method, " ", request_url, "] => ", route.controller, ".", route.action);

                if (mInfo == null)
                {
                    // if no method - just call FW.parser(hf) - show template from /route.controller/route.action dir
                    logger(LogLevel.DEBUG, "DEFAULT PARSER");
                    parser(new Hashtable());
                }
                else
                    callController(calledType, mInfo, args);
            }
            // logger(LogLevel.INFO, "NO EXCEPTION IN dispatch")

            catch (RedirectException Ex)
            {
                // not an error, just exit via Redirect
                logger(LogLevel.INFO, "Redirected...");
            }

            catch (AuthException Ex)
            {
                logger(LogLevel.DEBUG, Ex.Message);
                // if not logged - just redirect to login 
                if (SessionBool("is_logged") != true)
                    redirect((string)config("UNLOGGED_DEFAULT_URL"), false);
                else
                    errMsg(Ex.Message);
            }

            catch (ApplicationException Ex)
            {

                // get very first exception
                string msg = Ex.Message;
                Exception iex = Ex;
                while (iex.InnerException != null)
                {
                    iex = iex.InnerException;
                    msg = iex.Message;
                }

                if ((iex) is RedirectException)
                    // not an error, just exit via Redirect - TODO - remove here as already handled above?
                    logger(LogLevel.DEBUG, "Redirected...");
                else if ((iex) is UserException)
                {
                    // no need to log/report detailed user exception
                    logger(LogLevel.INFO, "UserException: " + msg);
                    errMsg(msg, iex);
                }
                else
                {
                    // it's ApplicationException, so just warning
                    logger(LogLevel.WARN, "===== ERROR DUMP APP =====");
                    logger(LogLevel.WARN, Ex.Message);
                    logger(LogLevel.WARN, Ex.ToString());
                    logger(LogLevel.WARN, "REQUEST FORM:", FORM);
                    logger(LogLevel.WARN, "SESSION:", context.Session);

                    // send_email_admin("App Exception: " & Ex.ToString() & vbCrLf & vbCrLf & _
                    // "Request: " & req.Path & vbCrLf & vbCrLf & _
                    // "Form: " & dumper(FORM) & vbCrLf & vbCrLf & _
                    // "Session:" & dumper(SESSION))

                    errMsg(msg, Ex);
                }
            }

            catch (Exception Ex)
            {
                // it's general Exception, so something more severe occur, log as error and notify admin
                logger(LogLevel.ERROR, "===== ERROR DUMP =====");
                logger(LogLevel.ERROR, Ex.Message);
                logger(LogLevel.ERROR, Ex.ToString());
                logger(LogLevel.ERROR, "REQUEST FORM:", FORM);
                logger(LogLevel.ERROR, "SESSION:", context.Session);

                //send_email_admin("Exception: " + Ex.ToString() + System.Environment.NewLine + System.Environment.NewLine
                //    + "Request: " + req.Path + System.Environment.NewLine + System.Environment.NewLine
                //    + "Form: " + dumper(FORM) + System.Environment.NewLine + System.Environment.NewLine
                //    + "Session:" + dumper(context.Session));

                if (Utils.f2int(this.config("log_level")) >= (int)LogLevel.DEBUG)
                    throw;
                else
                    errMsg("Server Error. Please, contact site administrator!", Ex);
            }

            TimeSpan end_timespan = DateTime.Now - start_time;
            logger(LogLevel.INFO, "REQUEST END   [", route.method, " ", request_url, "] in ", end_timespan.TotalSeconds, "s, ", string.Format("{0:0.000}", 1 / end_timespan.TotalSeconds), "/s, ", DB.SQL_QUERY_CTR, " SQL");
        }

        // simple auth check based on /controller/action - and rules filled in in Config class
        // called from Dispatcher
        // throws exception OR if is_die=false
        // return 2 - if user allowed to see page - explicitly based on fw.config
        // return 1 - if no fw.config rule, so need to further check Controller.access_level (not checking here for performance reasons)
        // return 0 - if not allowed
        public int _auth(string controller, string action, bool is_die = true)
        {
            int result = 0;

            // integrated XSS check - only for POST/PUT/DELETE requests 
            // OR for standard actions: Save, Delete, SaveMulti
            // OR if it contains XSS param
            if ((FORM.ContainsKey("XSS")
                || route.method == "POST"
                || route.method == "PUT"
                || route.method == "DELETE"
                || action == "Save"
                || action == "Delete"
                || action == "SaveMulti")
                && !string.IsNullOrEmpty(Session("XSS")) && Session("XSS") != (string)FORM["XSS"])
            {
                // XSS validation failed - check if we are under xss-excluded controller
                Hashtable no_xss = (Hashtable)this.config("no_xss");
                if (no_xss == null || !no_xss.ContainsKey(controller))
                {
                    if (is_die)
                        throw new AuthException("XSS Error. Reload the page or try to re-login");
                    return result;
                }
            }

            string path = "/" + controller + "/" + action;
            string path2 = "/" + controller;

            // pre-check controller's access level by url
            int current_level = Utils.f2int(Session("access_level"));

            Hashtable rules = (Hashtable)config("access_levels");
            if (rules != null && rules.ContainsKey(path))
            {
                if (current_level >= Utils.f2int(rules[path]))
                    result = 2;
            }
            else if (rules != null && rules.ContainsKey(path2))
            {
                if (current_level >= Utils.f2int(rules[path2]))
                    result = 2;
            }
            else
            {
                result = 1; // need to check Controller.access_level after _auth
            }

            if (result == 0 && is_die)
                throw new AuthException("Bad access - Not authorized");
            return result;
        }

        // parse query string, form and json in request body into fw.FORM
        private void parseForm()
        {
            Hashtable input = new();

            foreach (string s in req.Query.Keys)
            {
                if (s != null)
                    input[s] = req.Query[s].ToString();
            }

            if (req.HasFormContentType)
            {
                foreach (string s in req.Form.Keys)
                {
                    if (s != null)
                        input[s] = req.Form[s].ToString();
                }
            }

            // after perpare_FORM - grouping for names like XXX[YYYY] -> FORM{XXX}=@{YYYY1, YYYY2, ...}
            Hashtable SQ = new();
            string k;
            string sk;

            Hashtable f = new();
            foreach (string s in input.Keys)
            {
                Match m = Regex.Match(s, @"^([^\]]+)\[([^\]]+)\]$");
                if (m.Groups.Count > 1)
                {
                    // complex name
                    k = m.Groups[1].ToString();
                    sk = m.Groups[2].ToString();
                    if (!SQ.ContainsKey(k))
                        SQ[k] = new Hashtable();
                    ((Hashtable)SQ[k])[sk] = input[s];
                }
                else
                    f[s] = input[s];
            }

            foreach (string s in SQ.Keys)
                f[s] = SQ[s];

            // also parse json in request body if any
            if (req.ContentType != null && req.ContentType.Substring(0, "application/json".Length) == "application/json")
            {
                try
                {
                    // also could try this with Utils.json_decode
                    req.Body.Position = 0;
                    var json = new System.IO.StreamReader(req.Body).ReadToEnd();
                    Hashtable h = JsonSerializer.Deserialize<Hashtable>(json);
                    logger(LogLevel.TRACE, "REQUESTED JSON:", h);
                    Utils.mergeHash(ref f, ref h);
                }
                catch (Exception)
                {
                    logger(LogLevel.WARN, "Request JSON parse error");
                }
            }

            // logger(f)
            FORM = f;
        }

        public void logger(params object[] args)
        {
            if (args.Length == 0)
                return;
            _logger(LogLevel.DEBUG, ref args);
        }
        public void logger(LogLevel level, params object[] args)
        {
            if (args.Length == 0)
                return;
            _logger(level, ref args);
        }

        // internal logger routine, just to avoid pass args by value 2 times
        public void _logger(LogLevel level, ref object[] args)
        {
            // skip logging if requested level more than config's debug level
            if (level > (LogLevel)this.config("log_level"))
                return;

            StringBuilder str = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            str.Append(" ").Append(level.ToString()).Append(" ");
            str.Append(System.Diagnostics.Process.GetCurrentProcess().Id).Append(" ");
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(true);

            try
            {
                var i = 1;
                System.Diagnostics.StackFrame sf = st.GetFrame(i);
                // skip logger methods and DB internals as we want to know line where logged thing actually called from
                while (sf.GetMethod().Name == "logger" || (sf.GetFileName() ?? "").Substring((sf.GetFileName() ?? "").Length - 6) == @"\DB.vb")
                {
                    i += 1;
                    sf = st.GetFrame(i);
                }
                string fname = sf.GetFileName();
                if (fname != null)
                    str.Append(fname.Replace((string)this.config("site_root"), "").Replace(@"\App_Code", ""));
                str.Append(':').Append(sf.GetMethod().Name).Append(' ').Append(sf.GetFileLineNumber().ToString()).Append(" # ");
            }
            catch (Exception ex)
            {
                str.Append(" ... #" + ex.Message);
            }

            foreach (object dmp_obj in args)
                str.Append(dumper(dmp_obj));

            // write to debug console first
            System.Diagnostics.Debug.WriteLine(str);

            // write to log file
            string log_file = (string)config("log");
            if (!string.IsNullOrEmpty(log_file))
            {
                try
                {
                    // keep log file open to avoid overhead
                    if (floggerFS == null)
                    {
                        // open log with shared read/write so loggers from other processes can still write to it
                        floggerFS = new FileStream(log_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        floggerSW = new System.IO.StreamWriter(floggerFS)
                        {
                            AutoFlush = true
                        };
                    }
                    // force seek to end just in case other process added to file
                    floggerFS.Seek(0, SeekOrigin.End);
                    floggerSW.WriteLine(str.ToString());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("WARN logger can't write to log file. Reason:" + ex.Message);
                }
            }
        }

        public static string dumper(object dmp_obj, int level = 0) // TODO better type detection(suitable for all collection types)
        {
            StringBuilder str = new StringBuilder();
            if (dmp_obj == null)
                return "[Nothing]";
            if (level > 10)
                return "[Too Much Recursion]";

            try
            {
                Type type = dmp_obj.GetType();
                TypeCode typeCode = Type.GetTypeCode(type);
                string intend = new StringBuilder().Insert(0, "    ", level).Append(" ").ToString();

                level += 1;
                if (typeCode.ToString() == "Object")
                {
                    str.Append(System.Environment.NewLine);
                    if (dmp_obj is IList)
                    {
                        str.Append(intend + "[" + System.Environment.NewLine);
                        foreach (object v in (IList)dmp_obj)
                            str.Append(intend + " " + dumper(v, level) + System.Environment.NewLine);
                        str.Append(intend + "]" + System.Environment.NewLine);
                    }
                    else if (dmp_obj is IDictionary)
                    {
                        str.Append(intend + "{" + System.Environment.NewLine);
                        foreach (object k in ((IDictionary)dmp_obj).Keys)
                            str.Append(intend + " " + k + " => " + dumper(((IDictionary)dmp_obj)[k], level) + System.Environment.NewLine);
                        str.Append(intend + "}" + System.Environment.NewLine);
                    }
                    else if (dmp_obj is ISession)
                    {
                        str.Append(intend + "{" + System.Environment.NewLine);
                        foreach (string k in ((ISession)dmp_obj).Keys)
                            str.Append(intend + " " + k + " => " + dumper(((ISession)dmp_obj).GetString(k), level) + System.Environment.NewLine);
                        str.Append(intend + "}" + System.Environment.NewLine);
                    }
                    else
                        str.Append(intend + Utils.jsonEncode(dmp_obj, true) + System.Environment.NewLine);
                }
                else
                    str.Append(dmp_obj.ToString());
            }
            catch (Exception ex)
            {
                str.Append("***cannot dump object***" + ex.Message);
            }

            return str.ToString();
        }

        // return file content OR "" if no file exists or some other error happened (see errorInfo)
        /// <summary>
        /// return file content OR ""
        /// </summary>
        /// <param name="filename"></param>
        /// <returns></returns>
        public static string getFileContent(string filename)
        {
            string result = "";
            filename = Regex.Replace(filename, "/", @"\");
            if (!File.Exists(filename))
                return result;

            try
            {
                result = File.ReadAllText(filename);
            }
            catch (Exception Ex)
            {
                // TODO logger("ERROR", "Error getting file content [" & file_name & "]")
                //TODO MIGRATE set fw.last_file_error ?
                //errInfo = Ex.Message;
            }
            return result;
        }

        /// <summary>
        /// return array of file lines OR empty array if no file exists or some other error happened (see errorInfo)
        /// </summary>
        /// <param name="filename"></param>
        /// <returns></returns>
        public static string[] getFileLines(string filename)
        {
            string[] result = Array.Empty<string>();
            try
            {
                result = File.ReadAllLines(filename);
            }
            catch (Exception ex)
            {
                // TODO logger("ERROR", "Error getting file content [" & file_name & "]")
                //TODO MIGRATE set fw.last_file_error ?
                //errInfo = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// replace or append file content
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="fileData"></param>
        /// <param name="isAppend">False by default </param>
        public static void setFileContent(string filename, ref string fileData, bool isAppend = false)
        {
            filename = Regex.Replace(filename, "/", @"\");

            using (StreamWriter sw = new StreamWriter(filename, isAppend))
            {
                sw.Write(fileData);
            }
        }

        public async void responseWrite(string str)
        {
            await HttpResponseWritingExtensions.WriteAsync(this.resp, str);
        }

        // show page from template  /route.controller/route.action = parser('/route.controller/route.action/', $ps)
        public void parser(Hashtable hf)
        {
            this.parser((route.controller_path + "/" + route.action).ToLower(), hf);
        }

        // same as parsert(hf), but with base dir param
        // output format based on requested format: json, pjax or (default) full page html
        // for automatic json response support - set hf("_json") = True OR set hf("_json")=ArrayList/Hashtable - if json requested, only _json content will be returned
        // to override page template - set hf("_layout")="another_page_layout.html" (relative to SITE_TEMPLATES dir)
        // (not for json) to perform route_redirect - set hf("_route_redirect")("method"), hf("_route_redirect")("controller"), hf("_route_redirect")("args")
        // (not for json) to perform redirect - set hf("_redirect")="url"
        // TODO - create another func and call it from call_controller for processing _redirect, ... (non-parsepage) instead of calling parser?
        public void parser(string bdir, Hashtable ps)
        {
            //TODO MIGRATE this.resp.CacheControl = cache_control;

            string format = this.getResponseExpectedFormat();
            if (format == "json")
            {
                if (ps.ContainsKey("_json"))
                {
                    if (ps["_json"] is bool && (bool)ps["_json"] == true)
                    {
                        ps.Remove("_json"); // remove internal flag
                        this.parserJson(ps);
                    }
                    else
                        this.parserJson(ps["_json"]);// if _json exists - return only this element content
                }
                else
                {
                    ps = new Hashtable()
                    {
                        {
                            "success", false
                        },
                        {
                            "message", @"JSON response is not enabled for this Controller.Action (set ps[""_json""])=True or ps[""_json""])=data... to enable)."
                        }
                    };
                    this.parserJson(ps);
                }
                return; // no further processing for json
            }

            if (ps.ContainsKey("_route_redirect"))
            {
                Hashtable rr = (Hashtable)ps["_route_redirect"];
                this.routeRedirect((string)rr["method"], (string)rr["controller"], (object[])rr["args"]);
                return; // no further processing
            }

            if (ps.ContainsKey("_redirect"))
            {
                this.redirect((string)ps["_redirect"]);
                return; // no further processing
            }

            if (this.FERR.Count > 0 && !ps.ContainsKey("ERR"))
                ps["ERR"] = this.FERR; // add errors if any

            string layout;
            if (format == "pjax")
                layout = (string)G["PAGE_LAYOUT_PJAX"];
            else
                layout = (string)G["PAGE_LAYOUT"];

            if (ps.ContainsKey("_layout"))
                layout = (string)ps["_layout"];
            _parser(bdir, layout, ps);
        }

        // - show page from template  /controller/action = parser('/controller/action/', $layout, $ps)
        public void parser(string bdir, string tpl_name, Hashtable ps)
        {
            ps["_layout"] = tpl_name;
            parser(bdir, ps);
        }

        // actually uses ParsePage
        public void _parser(string bdir, string tpl_name, Hashtable hf)
        {
            logger(LogLevel.DEBUG, "parsing page bdir=", bdir, ", tpl=", tpl_name);
            ParsePage parser_obj = new ParsePage(this);
            string page = parser_obj.parse_page(bdir, tpl_name, hf);
            responseWrite(page);
        }

        public void parserJson(object ps)
        {
            ParsePage parser_obj = new ParsePage(this);
            string page = parser_obj.parse_json(ps);
            resp.Headers.Add("Content-type", "application/json; charset=utf-8");
            responseWrite(page);
        }

        // perform redirect
        // if is_exception=True (default) - throws RedirectException, so current request processing can end early
        public void redirect(string url, bool is_exception = true)
        {
            if (Regex.IsMatch(url, "^/"))
                url = this.config("ROOT_URL") + url;
            resp.Redirect(url, false);
            if (is_exception)
                throw new RedirectException();
        }

        public void routeRedirect(string action, string controller, object[] args = null)
        {
            setController((!string.IsNullOrEmpty(controller) ? controller : route.controller), action);

            Type calledType = Type.GetType(FW_NAMESPACE_PREFIX + route.controller + "Controller", true);
            MethodInfo mInfo = calledType.GetMethod(route.action + "Action");
            if (mInfo == null)
            {
                logger(LogLevel.INFO, "No method found for controller.action=[", route.controller, ".", route.action, "], displaying static page from related templates");
                // no method found - set to default Index method
                // route.action = "Index"
                // mInfo = calledType.GetMethod(route.action & "Action")

                // if no method - show template from /route.controller/route.action dir
                parser("/" + route.controller.ToLower() + "/" + route.action.ToLower(), new Hashtable());
            }

            if (mInfo != null)
                callController(calledType, mInfo, args);
        }
        // same as above just with default controller
        public void routeRedirect(string action, object[] args = null)
        {
            routeRedirect(action, route.controller, args);
        }

        /// <summary>
        /// set route.controller and optionally route.action, updates G too
        /// </summary>
        /// <param name="controller"></param>
        /// <param name="action"></param>
        public void setController(string controller, string action = "")
        {
            route.controller = controller;
            if (!string.IsNullOrEmpty(action))
                route.action = action;

            G["controller"] = route.controller;
            G["action"] = route.action;
            G["controller.ction"] = route.controller + "." + route.action;
        }

        // Call controller
        public void callController(Type calledType, MethodInfo mInfo, object[] args = null)
        {
            // check if method accept agrs and don't pass args if no args expected
            System.Reflection.ParameterInfo[] @params = mInfo.GetParameters();
            if (@params.Length == 0)
                args = null;

            FwController new_controller = (FwController)Activator.CreateInstance(calledType);
            new_controller.init(this);
            Hashtable ps = null;
            try
            {
                ps = (Hashtable)mInfo.Invoke(new_controller, args);
            }
            catch (TargetInvocationException ex)
            {
                // ignore redirect exception
                if (ex.InnerException == null || !((ex.InnerException) is RedirectException))
                    throw; // this keeps stack, also see http://weblogs.asp.net/fmarguerie/rethrowing-exceptions-and-preserving-the-full-call-stack-trace
            }
            if (ps != null)
                parser(ps);
        }


        public void fileResponse(string filepath, string attname, string ContentType = "application/octet-stream", string ContentDisposition = "attachment")
        {
            logger(LogLevel.DEBUG, "sending file response  = ", filepath, " as ", attname);
            attname = Regex.Replace(attname, @"[^\w. \-]+", "_");
            resp.Headers.Add("Content-type", ContentType);
            resp.Headers.Add("Content-Length", Utils.fileSize(filepath).ToString());
            resp.Headers.Add("Content-Disposition", ContentDisposition + "; filename=\"" + attname + "\"");
            resp.SendFileAsync(filepath);
            //TODO MIGRATE test if necessary resp.OutputStream.Close();
        }

        // SEND EMAIL
        // mail_to may contain several emails delimited by ;
        // filenames (optional) - human filename => hash filepath
        // aCC - arraylist of CC addresses (strings)
        // reply_to - optional reply to email
        // options - hashtable with options: 
        // "read-receipt"
        // RETURN:
        // true if sent successfully
        // false if some problem occured (see log)
        public bool sendEmail(string mail_from, string mail_to, string mail_subject, string mail_body, Hashtable filenames = null, ArrayList aCC = null, string reply_to = "", Hashtable options = null)
        {
            bool result = true;
            MailMessage message = null/* TODO Change to default(_) if this is not a reference type */;
            if (options == null)
                options = new Hashtable();

            try
            {
                if (mail_from.Length == 0)
                    mail_from = (string)this.config("mail_from"); // default mail from
                mail_subject = Regex.Replace(mail_subject, @"[\r\n]+", " ");

                bool is_test = Utils.f2bool(this.config("is_test"));
                if (is_test)
                {
                    string test_email = (string)this.config("test_email");
                    mail_body = "TEST SEND. PASSED MAIL_TO=[" + mail_to + "]" + System.Environment.NewLine + mail_body;
                    mail_to = (string)this.config("test_email");
                    logger(LogLevel.INFO, "EMAIL SENT TO TEST EMAIL [", mail_to, "] - TEST ENABLED IN web.config");
                }

                logger(LogLevel.INFO, "Sending email. From=[", mail_from, "], ReplyTo=[", reply_to, "], To=[", mail_to, "], Subj=[", mail_subject, "]");
                logger(LogLevel.DEBUG, mail_body);

                if (!string.IsNullOrEmpty(mail_to))
                {
                    message = new MailMessage();
                    if (options.ContainsKey("read-receipt"))
                        message.Headers.Add("Disposition-Notification-To", mail_from);

                    // detect HTML body - if it's started with <!DOCTYPE or <html tags
                    if (Regex.IsMatch(mail_body, @"^\s*<(!DOCTYPE|html)[^>]*>", RegexOptions.IgnoreCase))
                        message.IsBodyHtml = true;

                    message.From = new MailAddress(mail_from);
                    message.Subject = mail_subject;
                    message.Body = mail_body;
                    // If reply_to > "" Then message.ReplyTo = New MailAddress(reply_to) '.net<4
                    if (!string.IsNullOrEmpty(reply_to))
                        message.ReplyToList.Add(reply_to); // .net>=4

                    // mail_to may contain several emails delimited by ;
                    ArrayList amail_to = Utils.splitEmails(mail_to);
                    foreach (string email1 in amail_to)
                    {
                        string email = email1.Trim();
                        if (string.IsNullOrEmpty(email))
                            continue;
                        message.To.Add(new MailAddress(email));
                    }

                    // add CC if any
                    if (aCC != null)
                    {
                        if (is_test)
                        {
                            foreach (string cc in aCC)
                            {
                                logger(LogLevel.INFO, "TEST SEND. PASSED CC=[", cc, "]");
                                message.CC.Add(new MailAddress(mail_to));
                            }
                        }
                        else
                            foreach (string cc1 in aCC)
                            {
                                string cc = cc1.Trim();
                                if (string.IsNullOrEmpty(cc))
                                    continue;
                                message.CC.Add(new MailAddress(cc));
                            }
                    }

                    // attach attachments if any
                    if (filenames != null)
                    {
                        // sort by human name
                        ArrayList fkeys = new ArrayList(filenames.Keys);
                        fkeys.Sort();
                        foreach (string human_filename in fkeys)
                        {
                            string filename = (string)filenames[human_filename];
                            System.Net.Mail.Attachment att = new System.Net.Mail.Attachment(filename, System.Net.Mime.MediaTypeNames.Application.Octet)
                            {
                                Name = human_filename,
                                NameEncoding = System.Text.Encoding.UTF8
                            };
                            // att.ContentDisposition.FileName = human_filename
                            logger(LogLevel.DEBUG, "attachment ", human_filename, " => ", filename);
                            message.Attachments.Add(att);
                        }
                    }

                    using (SmtpClient client = new SmtpClient())
                    {
                        client.Send(message);
                    }
                }
            }
            catch (Exception ex)
            {
                result = false;
                last_error_send_email = ex.Message;
                if (ex.InnerException != null)
                    last_error_send_email += " " + ex.InnerException.Message;
                logger(LogLevel.ERROR, "send_email error:", last_error_send_email);
            }
            finally
            {
                if (message != null)
                    message.Dispose();
            }// important, as this will close any opened attachment files
            return result;
        }

        // shortcut for send_email from template from the /emails template dir
        public bool sendEmailTpl(string mail_to, string tpl, Hashtable hf, Hashtable filenames = null/* TODO Change to default(_) if this is not a reference type */, ArrayList aCC = null/* TODO Change to default(_) if this is not a reference type */, string reply_to = "")
        {
            ParsePage parser_obj = new(this);
            Regex r = new Regex(@"[\n\r]+");
            string subj_body = parser_obj.parse_page("/emails", tpl, hf);
            if (subj_body.Length == 0)
                throw new ApplicationException("No email template defined [" + tpl + "]");
            string[] arr = r.Split(subj_body, 2);
            return sendEmail("", mail_to, arr[0], arr[1], filenames, aCC, reply_to);
        }

        // send email message to site admin (usually used in case of errors)
        public void sendEmailAdmin(string msg)
        {
            this.sendEmail("", (string)this.config("admin_email"), msg.Substring(0, 512), msg);
        }

        public string loadUrl(string url, Hashtable @params = null)
        {
            System.Net.WebClient client = new System.Net.WebClient();
            string content;
            if (@params != null)
            {
                // POST
                NameValueCollection nv = new NameValueCollection();
                foreach (string key in @params.Keys)
                    nv.Add(key, (string)@params[key]);
                content = (new System.Text.UTF8Encoding()).GetString(client.UploadValues(url, "POST", nv));
            }
            else
                // GET
                content = client.DownloadString(url);

            return content;
        }

        public void errMsg(string msg, Exception Ex = null)
        {
            Hashtable ps = new Hashtable();
            var tpl_dir = "/error";

            /* TODO ERROR: Skipped IfDirectiveTrivia *//* TODO ERROR: Skipped DisabledTextTrivia *//* TODO ERROR: Skipped EndIfDirectiveTrivia */
            ps["err_time"] = DateTime.Now;
            ps["err_msg"] = msg;
            if (Utils.f2bool(this.config("IS_DEV")))
            {
                ps["is_dump"] = true;
                if (Ex != null)
                    ps["DUMP_STACK"] = Ex.ToString();
                ps["DUMP_FORM"] = dumper(FORM);
                ps["DUMP_SESSION"] = dumper(context.Session);
            }

            ps["success"] = false;
            ps["message"] = msg;
            ps["_json"] = true;

            if (Ex is ApplicationException)
                this.resp.StatusCode = 500;
            else if (Ex is UserException)
                this.resp.StatusCode = 403;

            parser(tpl_dir, ps);
        }

        // return model object by type
        // CACHED in fw.models, so it's singletones
        public T model<T>() where T : new()
        {
            Type tt = typeof(T);
            if (!models.ContainsKey(tt.Name))
            {
                T m = new();

                // initialize
                typeof(T).GetMethod("init").Invoke(m, new object[] { this });

                models[tt.Name] = m;
            }
            return (T)models[tt.Name];
        }

        // return model object by model name
        public FwModel model(string model_name)
        {
            if (!models.ContainsKey(model_name))
            {
                FwModel m = (FwModel)Activator.CreateInstance(Type.GetType(FW_NAMESPACE_PREFIX + model_name));
                // initialize
                m.init(this);
                models[model_name] = m;
            }
            return (FwModel)models[model_name];
        }

        public void logEvent(string ev_icode, int item_id = 0, int item_id2 = 0, string iname = "", int records_affected = 0, Hashtable changed_fields = null/* TODO Change to default(_) if this is not a reference type */)
        {
            if (!is_log_events)
                return;
            this.model<FwEvents>().log(ev_icode, item_id, item_id2, iname, records_affected, changed_fields);
        }

        public void rw(string str)
        {
            this.responseWrite(str + "<br>" + System.Environment.NewLine);
            this.resp.Body.FlushAsync();
        }


        private bool disposedValue; // To detect redundant calls

        // IDisposable
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // dispose managed state (managed objects).
                    if (sentryClient != null)
                        sentryClient.Dispose();
                }

                // free unmanaged resources (unmanaged objects) and override Finalize() below.
                try
                {
                    db.Dispose(); // this will return db connections to pool

                    long log_length = 0;
                    if (floggerFS != null)
                        log_length = floggerFS.Length;

                    if (floggerSW != null)
                        floggerSW.Close(); // no need to close floggerFS as StreamWriter closes it
                    if (floggerFS != null)
                    {
                        floggerFS.Close();

                        // check if log file too large and need to be rotated
                        var max_log_size = Utils.f2int(config("log_max_size"));
                        if (max_log_size > 0 && log_length > max_log_size)
                        {
                            var to_path = config("log") + ".1";
                            File.Delete(to_path);
                            File.Move((string)config("log"), to_path);
                        }
                    }
                }
                // TODO: set large fields to null.
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("exception in Dispose:" + ex.Message);
                }
            }
            disposedValue = true;
        }

        // override Finalize() only if Dispose(disposing As Boolean) above has code to free unmanaged resources.
        ~FW()
        {
            // Do not change this code.  Put cleanup code in Dispose(disposing As Boolean) above.
            Dispose(false);
        }

        // This code added by Visual Basic to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code.  Put cleanup code in Dispose(disposing As Boolean) above.
            Dispose(true);
            // uncomment the following line if Finalize() is overridden above.
            GC.SuppressFinalize(this);
        }
    }
}