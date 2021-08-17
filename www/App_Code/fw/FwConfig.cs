﻿// App Configuration class
//
// Part of ASP.NET osa framework  www.osalabs.com/osafw/asp.net
// (c) 2009-2021 Oleg Savchuk www.osalabs.com

using System;
using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualBasic; //TODO MIGRATE get rid of

namespace osafw
{
    public class FwConfig
    {
        public static string hostname;
        public static Hashtable settings;
        public static string route_prefixes_rx = "";
        public static IConfiguration configuration;

        private static readonly object locker = new object();

        public static void init(HttpContext context, IConfiguration configuration, string hostname = "")
        {
            // appSettings is Shared, so it's lifetime same as application lifetime 
            // if appSettings already initialized no need to read web.config again
            lock (locker)
            {
                if (settings != null && settings.Count > 0 && settings.ContainsKey("_SETTINGS_OK"))
                    return;
                FwConfig.configuration = configuration;
                FwConfig.hostname = hostname;
                initDefaults(context, hostname);
                readSettings();
                specialSettings();

                settings["_SETTINGS_OK"] = true; // just a marker to ensure we have all settings set
            }
        }

        // reload settings
        public static void reload()
        {
            initDefaults(FW.Current.context, FwConfig.hostname);
            readSettings();
            specialSettings();
        }

        // init default settings
        private static void initDefaults(HttpContext context, string hostname = "")
        {
            settings = new Hashtable();
            HttpRequest req = context.Request;

            if (string.IsNullOrEmpty(hostname))
                hostname = context.GetServerVariable("HTTP_HOST");
            settings["hostname"] = hostname;

            string ApplicationPath = req.PathBase; //TODO MIGRATE test with IIS subfolder if this is correct variable
            settings["ROOT_URL"] = Regex.Replace(ApplicationPath, @"\/$", ""); // removed last / if any
            string PhysicalApplicationPath = AppDomain.CurrentDomain.BaseDirectory.Substring(0, AppDomain.CurrentDomain.BaseDirectory.IndexOf(@"\bin"));//TODO MIGRATE what is bin???
            settings["site_root"] = Regex.Replace(PhysicalApplicationPath, @"\\$", ""); // removed last \ if any

            settings["template"] = settings["site_root"] + @"\App_Data\template";
            settings["log"] = settings["site_root"] + @"\App_Data\logs\main.log";
            settings["log_max_size"] = 100 * 1024 * 1024; // 100 MB is max log size
            settings["tmp"] = Path.GetTempPath();

            string http = "http://";
            if (context.GetServerVariable("HTTPS") == "on")
                http = "https://";
            string port = ":" + context.GetServerVariable("SERVER_PORT");
            if (port == ":80" || port == ":443")
                port = "";
            settings["ROOT_DOMAIN"] = http + context.GetServerVariable("SERVER_NAME") + port;
        }

        private static void readSettingsSection(IConfigurationSection section, ref Hashtable settings)
        {
            if (section.Value != null)
            {
                settings[section.Key] = section.Value;
            }
            else if (section.Key != null)
            {
                settings[section.Key] = new Hashtable();
                foreach (IConfigurationSection sub_section in section.GetChildren())
                {
                    Hashtable s = (Hashtable)settings[section.Key];
                    readSettingsSection(sub_section, ref s);
                }
            }
        }

        // read setting into appSettings
        private static void readSettings()
        {
            var valuesSection = configuration.GetSection("appSettings");
            foreach (IConfigurationSection section in valuesSection.GetChildren())
            {
                readSettingsSection(section, ref settings);
            }

            //TODO MIGRATE decide - this is probably not necessary as appSettings already in json
            //NameValueCollection appSettings = ConfigurationManager.AppSettings();

            //string[] keys = appSettings.AllKeys;
            //foreach (string key in keys)
            //    parseSetting(key, appSettings[key]);
        }

        //TODO MIGRATE decide - this is probably not necessary as appSettings already in json
        private static void parseSetting(string key, string value)
        {
            string delim = "|";
            if (Strings.InStr(key, delim) == 0)
                settings[key] = parseSettingValue(ref value);
            else
            {
                string[] keys = Strings.Split(key, delim);

                // build up all hashtables tree
                Hashtable ptr = settings;
                for (int i = 0; i <= keys.Length - 2; i++)
                {
                    string hkey = keys[i];
                    if (ptr.ContainsKey(hkey) && ptr is Hashtable)
                        ptr = (Hashtable)ptr[hkey]; // going deep into
                    else
                    {
                        ptr[hkey] = new Hashtable(); // this will overwrite any value, i.e. settings names must be different on same level
                        ptr = (Hashtable)ptr[hkey];
                    }
                }
                // assign value to key element in deepest hashtree
                ptr[keys[keys.Length - 1]] = parseSettingValue(ref value);
            }
        }

        // parse value to type, supported:
        // boolean
        // int
        // qh - using Utils.qh()
        private static object parseSettingValue(ref string value)
        {
            object result;
            Match m = Regex.Match(value, "^~(.*?)~");
            if (m.Success)
            {
                string value2 = Regex.Replace(value, "^~.*?~", "");
                switch (m.Groups[1].Value)
                {
                    case "int":
                        {
                            if (!int.TryParse(value2, out int ival))
                                ival = 0;
                            result = ival;
                            break;
                        }

                    case "boolean":
                        {
                            bool ibool;
                            if (!bool.TryParse(value2, out ibool))
                                ibool = false;
                            result = ibool;
                            break;
                        }

                    case "qh":
                        {
                            result = Utils.qh(value2);
                            break;
                        }

                    default:
                        {
                            result = value2;
                            break;
                        }
                }
            }
            else
                result = new string(value); //copy of the string

            return result;
        }

        // set special settings after we read config
        private static void specialSettings()
        {
            string hostname = (string)settings["hostname"];

            Hashtable overs = (Hashtable)settings["override"];
            if (overs != null)
            {
                foreach (string over_name in overs.Keys)
                {
                    Hashtable over = (Hashtable)overs[over_name];
                    if (Regex.IsMatch(hostname, (string)over["hostname_match"]))
                    {
                        settings["config_override"] = over_name;
                        Utils.mergeHashDeep(ref settings, ref over);
                        break;
                    }
                }
            }

            // convert strings to specific types
            LogLevel log_level = LogLevel.INFO; // default log level if No or Wrong level in config
            if (settings.ContainsKey("log_level"))
                Enum.TryParse<LogLevel>((string?)settings["log_level"], true, out log_level);
            settings["log_level"] = log_level;

            // default settings that depend on other settings
            if (!settings.ContainsKey("ASSETS_URL"))
                settings["ASSETS_URL"] = settings["ROOT_URL"] + "/assets";
        }


        // prefixes used so Dispatcher will know that url starts not with a full controller name, but with a prefix, need to be added to controller name
        // return regexp str that cut the prefix from the url, second capturing group captures rest of url after the prefix
        public static string getRoutePrefixesRX()
        {
            if (string.IsNullOrEmpty(route_prefixes_rx))
            {
                // prepare regexp - escape all prefixes
                ArrayList r = new ArrayList();
                var route_prefixes = (Hashtable)settings["route_prefixes"];
                if (route_prefixes != null)
                {
                    foreach (string url in route_prefixes.Keys)
                        r.Add(Regex.Escape(url));

                    route_prefixes_rx = "^(" + string.Join("|", (string[])r.ToArray(typeof(string))) + ")(/.*)?$";
                }
            }

            return route_prefixes_rx;
        }
    }
}