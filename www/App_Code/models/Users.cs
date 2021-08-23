﻿// Users model class
//
// Part of ASP.NET osa framework  www.osalabs.com/osafw/asp.net
// (c) 2009-2021 Oleg Savchuk www.osalabs.com

using System;
using Microsoft.VisualBasic;
using System.Collections;
using static BCrypt.Net.BCrypt;
using System.Text.RegularExpressions;

namespace osafw
{
    public class Users : FwModel
    {
        // ACL constants
        public const int ACL_VISITOR = 0; //non-logged visitor
        public const int ACL_MEMBER = 1; //min access level for users
        public const int ACL_EMPLOYEE = 50;
        public const int ACL_MANAGER = 80;
        public const int ACL_ADMIN = 90;
        public const int ACL_SITEADMIN = 100;

        private readonly string table_menu_items = "menu_items";

        /// <summary>
        /// return current user id or 0 for non-logged users
        /// </summary>
        public static int id
        {
            get { return Utils.f2int(FW.Current.SessionInt("user_id")); }
        }

        /// <summary>
        /// return true if current user is logged
        /// </summary>
        public static bool isLogged
        {
            get { return Users.id > 0; }
        }

        public Users() : base()
        {
            table_name = "users";
            csv_export_fields = "id fname lname email add_time";
            csv_export_headers = "id,First Name,Last Name,Email,Registered";
        }

        public Hashtable oneByEmail(string email)
        {
            Hashtable where = new();
            where["email"] = email;
            Hashtable hU = db.row(table_name, where).toHashtable();
            return hU;
        }

        /// <summary>
        /// return full user name - First Name Last Name
        /// </summary>
        /// <param name="id">Object type because if upd_users_id could be null</param>
        /// <returns></returns>
        public new string iname(object id)
        {
            string result = "";

            int iid = Utils.f2int(id);
            if (iid > 0)
            {
                var item = one(iid);
                result = item["fname"] + "  " + item["lname"];
            }

            return result;
        }

        // check if user exists for a given email
        public override bool isExists(object uniq_key, int not_id)
        {
            return isExistsByField(uniq_key, not_id, "email");
        }

        public override int add(DBRow item)
        {
            if (!item.ContainsKey("access_level"))
                item["access_level"] = Utils.f2str(Users.ACL_MEMBER);

            if (!item.ContainsKey("pwd"))
                item["pwd"] = Utils.getRandStr(8); // generate password
            item["pwd"] = this.hashPwd((string)item["pwd"]);
            return base.add(item);
        }

        public override bool update(int id, DBRow item)
        {
            if (item.ContainsKey("pwd"))
                item["pwd"] = this.hashPwd((string)item["pwd"]);
            return base.update(id, item);
        }

        /// <summary>
        /// performs any required password cleaning (for now - just limit pwd length at 32 and trim)
        /// </summary>
        /// <param name="plain_pwd">non-encrypted plain pwd</param>
        /// <returns>clean plain pwd</returns>
        public string cleanPwd(string plain_pwd)
        {
            return Strings.Trim(Strings.Left(plain_pwd, 32));
        }

        /// <summary>
        /// generate password hash from plain password
        /// </summary>
        /// <param name="plain_pwd">plain pwd</param>
        /// <returns>hash using https://github.com/BcryptNet/bcrypt.net </returns>
        public string hashPwd(string plain_pwd)
        {
            try
            {
                return EnhancedHashPassword(cleanPwd(plain_pwd));
            }
            catch (Exception)
            {
            }
            return "";
        }

        /// <summary>
        /// return true if plain password has the same hash as provided
        /// </summary>
        /// <param name="plain_pwd">plain pwd from user input</param>
        /// <param name="pwd_hash">password hash previously generated by hashPwd</param>
        /// <returns></returns>
        public bool checkPwd(string plain_pwd, string pwd_hash)
        {
            try
            {
                return EnhancedVerify(cleanPwd(plain_pwd), pwd_hash);
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// generate reset token, save to users and send pwd reset link to the user
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public bool sendPwdReset(int id)
        {
            var pwd_reset_token = Utils.getRandStr(50);

            DBRow item = new()
            {
                {
                    "pwd_reset", this.hashPwd(pwd_reset_token)
                },
                {
                    "pwd_reset_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                }
            };
            this.update(id, item);

            var user = this.one(id);
            user["pwd_reset_token"] = pwd_reset_token;

            return fw.sendEmailTpl((string)user["email"], "email_pwd.txt", user.toHashtable());
        }

        /// <summary>
        /// evaluate password's stength and return a score (>60 good, >80 strong)
        /// </summary>
        /// <param name="pwd"></param>
        /// <returns></returns>
        public double scorePwd(string pwd)
        {
            var result = 0;
            if (string.IsNullOrEmpty(pwd))
                return result;

            // award every unique letter until 5 repetitions
            Hashtable chars = new();
            for (var i = 0; i <= pwd.Length - 1; i++)
            {
                chars[pwd[i]] = Utils.f2int(chars[pwd[i]]) + 1;
                result += (int)(5.0 / (double)chars[pwd[i]]);
            }

            // bonus points for mixing it up
            Hashtable vars = new()
            {
                {
                    "digits",
                    Regex.IsMatch(pwd, @"\d")
                },
                {
                    "lower",
                    Regex.IsMatch(pwd, "[a-z]")
                },
                {
                    "upper",
                    Regex.IsMatch(pwd, "[A-Z]")
                },
                {
                    "other",
                    Regex.IsMatch(pwd, @"\W")
                }
            };
            var ctr = 0;
            foreach (bool value in vars.Values)
            {
                if (value) ctr += 1;
            }
            result += (ctr - 1) * 10;

            // adjust for length
            result = (int)(Math.Log(pwd.Length) / Math.Log(8)) * result;

            return result;
        }

        // fill the session and do all necessary things just user authenticated (and before redirect
        public bool doLogin(int id)
        {
            fw.context.Session.Clear();
            fw.Session("XSS", Utils.getRandStr(16));

            reloadSession(id);

            fw.logEvent("login", id);
            // update login info
            DBRow fields = new();
            fields["login_time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            this.update(id, fields);
            return true;
        }

        public bool reloadSession(int id = 0)
        {
            if (id == 0)
                id = Users.id;
            DBRow user = one(id);

            fw.SessionInt("user_id", id);
            fw.Session("login", (string)user["email"]);
            fw.Session("access_level", (string)user["access_level"]); //note, set as string
            fw.Session("lang", (string)user["lang"]);
            // fw.SESSION("user", hU)
            var fname = ((string)user["fname"]).Trim();
            var lname = ((string)user["lname"]).Trim();
            if (!string.IsNullOrEmpty(fname) || !string.IsNullOrEmpty(lname))
                fw.Session("user_name", fname + Interaction.IIf(!string.IsNullOrEmpty(fname), " ", "") + lname);
            else
                fw.Session("user_name", (string)user["email"]);

            var avatar_link = "";
            if (Utils.f2int(user["att_id"]) > 0)
                avatar_link = fw.model<Att>().getUrl(Utils.f2int(user["att_id"]), "s");
            fw.Session("user_avatar_link", avatar_link);

            return true;
        }

        // return standard list of id,iname where status=0 order by iname
        public override ArrayList list()
        {
            string sql = "select id, fname+' '+lname as iname from " + db.q_ident(table_name) + " where status=0 order by fname, lname";
            return db.arrayp(sql, DB.h()).toArrayList();
        }
        public override ArrayList listSelectOptions(Hashtable def = null)
        {
            string sql = "select id, fname+' '+lname as iname from " + db.q_ident(table_name) + " where status=0 order by fname, lname";
            return db.arrayp(sql, DB.h()).toArrayList();
        }

        /// <summary>
        /// check if current user acl is enough. throw exception or return false if user's acl is not enough
        /// </summary>
        /// <param name="acl">minimum required access level</param>
        public bool checkAccess(int acl, bool is_die = true)
        {
            int users_acl = Utils.f2int(fw.Session("access_level"));

            // check access
            if (users_acl < acl)
            {
                if (is_die)
                    throw new ApplicationException("Access Denied");
                return false;
            }

            return true;
        }

        public void loadMenuItems()
        {
            ArrayList menu_items = (ArrayList)FwCache.getValue("menu_items");

            if (menu_items == null)
            {
                // read main menu items for sidebar
                menu_items = db.array(table_menu_items, DB.h("status", STATUS_ACTIVE), "iname").toArrayList();
                FwCache.setValue("menu_items", menu_items);
            }

            // only Menu items user can see per ACL
            var users_acl = Utils.f2int(fw.Session("access_level"));
            ArrayList result = new();
            foreach (Hashtable item in menu_items)
            {
                if (Utils.f2int(item["access_level"]) <= users_acl)
                    result.Add(item);
            }

            fw.G["menu_items"] = result;
        }
    }
}