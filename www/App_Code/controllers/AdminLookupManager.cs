﻿// LookupManager Admin controller
//
// Part of ASP.NET osa framework  www.osalabs.com/osafw/asp.net
// (c) 2009-2021 Oleg Savchuk www.osalabs.com

using System;
using System.Collections;
using Microsoft.VisualBasic;

namespace osafw
{
    public class AdminLookupManagerController : FwController
    {
        public static new int access_level = Users.ACL_MANAGER;

        protected LookupManager model = new();
        protected LookupManagerTables model_tables = new();
        protected string dict; // current lookup dictionary
        protected Hashtable defs;
        protected string dictionaries_url;
        private bool is_readonly = false;

        public override void init(FW fw)
        {
            base.init(fw);
            model.init(fw);
            model_tables = fw.model<LookupManagerTables>();
            required_fields = ""; // default required fields, space-separated
            base_url = "/Admin/LookupManager"; // base url for the controller
            dictionaries_url = base_url + "/(Dictionaries)";

            dict = reqs("d");
            defs = model_tables.oneByTname(dict);
            if (defs.Count == 0)
                dict = "";
        }

        private void check_dict()
        {
            if (string.IsNullOrEmpty(dict))
                fw.redirect(base_url + "/(Dictionaries)");
            if (!string.IsNullOrEmpty((string)defs["url"]))
                fw.redirect((string)defs["url"]);
        }

        public Hashtable DictionariesAction()
        {
            Hashtable ps = new();

            // code below to show list of items in columns instead of plain list

            int columns = 4;
            ArrayList tables = model_tables.list();
            int max_rows = (int)Math.Ceiling(tables.Count / (double)columns);
            ArrayList cols = new();

            // add rows
            int curcol = 0;
            foreach (Hashtable table in tables)
            {
                if (cols.Count <= curcol)
                    cols.Add(new Hashtable());
                Hashtable h = (Hashtable)cols[curcol];
                if (h.Count == 0)
                {
                    h["col_sm"] = Math.Floor(12 / (double)columns);
                    h["list_rows"] = new ArrayList();
                }
                ArrayList al = (ArrayList)h["list_rows"];
                al.Add(table);
                if (al.Count >= max_rows)
                    curcol += 1;
            }

            ps["list_сols"] = cols;
            return ps;
        }

        public Hashtable IndexAction()
        {
            check_dict();

            // if this is one-form dictionary - show edit form with first record
            if (Utils.f2int(defs["is_one_form"]) == 1)
            {
                string id_fname = fw.model<LookupManagerTables>().getColumnId(defs);
                var row = model.topByTname((string)defs["tname"]);
                // fw.redirect(base_url & "/" & row(id_fname) & "/edit/?d=" & dict)
                String[] args = (new[] { (string)row[id_fname] });
                fw.routeRedirect("ShowForm", null, args);
                return null;
            }

            // get columns
            ArrayList cols = model_tables.getColumns(defs);
            string list_table_name = (string)defs["tname"];
            // logger(defs)
            // logger(cols)

            Hashtable ps = new();
            ps["is_two_modes"] = true;
            Hashtable f = initFilter("_filter_lookupmanager_" + list_table_name);

            // sorting
            if (string.IsNullOrEmpty((string)f["sortby"]))
            {
                if (cols.Count > 0)
                    f["sortby"] = ((Hashtable)cols[0])["name"]; // by default - sort by first column
                else
                    f["sortby"] = "";
            }
            if ((string)f["sortdir"] != "desc")
                f["sortdir"] = "asc";
            Hashtable SORTSQL = new();
            ArrayList fields_headers = new();
            ArrayList group_headers = new();
            bool is_group_headers = false;

            Hashtable list_cols = new();
            if (!string.IsNullOrEmpty((string)defs["list_columns"]))
            {
                list_cols = Utils.commastr2hash((string)defs["list_columns"]);
                ps["is_two_modes"] = false; // if custom list defined - don't enable table edit mode
            }
            else
                // if no custom columns - remove sys cols
                cols = model.filterOutSysCols(cols);

            foreach (Hashtable col in cols)
            {
                SORTSQL[col["name"]] = db.q_ident((string)col["name"]);

                if (list_cols.Count > 0 && !list_cols.ContainsKey(col["name"]))
                    continue;

                Hashtable fh = new();
                fh["iname"] = col["iname"];
                fh["colname"] = col["name"];
                fh["maxlen"] = col["maxlen"];
                fh["type"] = col["itype"];
                if ((string)fh["type"] == "textarea")
                    fh["type"] = ""; // show textarea as inputtext in table edit mode

                if (col["itype"].ToString().Contains("."))
                {
                    // lookup type
                    fh["type"] = "lookup";
                    fh["select_options"] = model_tables.getLookupSelectOptions((string)col["itype"], "");
                }

                fields_headers.Add(fh);

                // detect/build group headers
                string igroup = col["igroup"].ToString().Trim();
                if (group_headers.Count == 0)
                {
                    Hashtable h = new();
                    h["iname"] = igroup;
                    h["colspan"] = 0;
                    group_headers.Add(h);
                }
                if (igroup == (string)((Hashtable)group_headers[group_headers.Count - 1])["iname"])
                    ((Hashtable)group_headers[group_headers.Count - 1])["colspan"] = (int)((Hashtable)group_headers[group_headers.Count - 1])["colspan"] + 1;
                else
                {
                    Hashtable h = new();
                    h["iname"] = igroup;
                    h["colspan"] = 1;
                    group_headers.Add(h);
                }

                if (!string.IsNullOrEmpty(igroup))
                    is_group_headers = true;
            }

            string where = " 1=1";
            if (!string.IsNullOrEmpty((string)f["s"]))
            {
                string slike = db.q("%" + f["s"] + "%");
                string swhere = "";
                foreach (Hashtable col in cols)
                    swhere += "or " + db.q_ident((string)col["name"]) + " like " + slike;
                if (!string.IsNullOrEmpty(swhere))
                    where += " and (0=1 " + swhere + ")";
            }

            ps["count"] = db.value("select count(*) from " + db.q_ident(list_table_name) + " where " + where);
            if ((int)ps["count"] > 0)
            {
                int offset = (int)f["pagenum"] * (int)f["pagesize"];
                int limit = (int)f["pagesize"];
                string orderby = (string)SORTSQL[(string)f["sortby"]];
                if (string.IsNullOrEmpty(orderby))
                    orderby = "1";
                if ((string)f["sortdir"] == "desc")
                {
                    if (orderby.Contains(","))
                        orderby = Strings.Replace(orderby, ",", " desc,");
                    orderby += " desc";
                }

                var sql = "SELECT * FROM " + db.q_ident(list_table_name) +
                          " WHERE " + where +
                          " ORDER BY " + orderby + " OFFSET " + offset + " ROWS " + " FETCH NEXT " + limit + " ROWS ONLY";

                ps["list_rows"] = db.array(sql);
                ps["pager"] = FormUtils.getPager((int)ps["count"], (int)f["pagenum"], f["pagesize"]);
                if (ps["pager"] != null)
                {
                    // add dict info for pager
                    foreach (Hashtable page in (ArrayList)ps["pager"])
                        page["d"] = dict;
                }

                // add/modify rows from db
                foreach (Hashtable row in (ArrayList)ps["list_rows"])
                {                    
                    // calc md5 first if in edit mode
                    if ((string)f["mode"] == "edit")
                        row["row_md5"] = model.getRowMD5(row);

                    row["is_readonly"] = is_readonly;
                    row["id"] = row[model_tables.getColumnId(defs)];
                    row["d"] = dict;
                    row["f"] = f;

                    ArrayList fv = new();
                    foreach (Hashtable col in cols)
                    {
                        if (list_cols.Count > 0 && !list_cols.ContainsKey(col["name"]))
                            continue;

                        Hashtable fh = new();
                        fh["colname"] = col["name"];
                        fh["iname"] = col["iname"];
                        fh["value"] = row[col["name"]];
                        if (list_cols.Count == 0 && ((string)col["name"] == "status" || (string)col["name"] == "iname"))
                            fh["is_custom"] = true;

                        fh["id"] = row["id"];
                        fh["maxlen"] = col["maxlen"];
                        fh["type"] = col["itype"];
                        if ((string)fh["type"] == "textarea")
                            fh["type"] = ""; // show textarea as inputtext in table edit mode

                        if (col["itype"].ToString().Contains("."))
                        {
                            // lookup type
                            fh["type"] = "lookup";
                            fh["select_options"] = model_tables.getLookupSelectOptions((string)col["itype"], fh["value"]);
                            // for lookup type display value should be from lookup table
                            fh["value"] = model_tables.getLookupValue((string)col["itype"], fh["value"]);
                        }

                        fv.Add(fh);
                    }
                    row["fields_values"] = fv;
                }
            }
            ps["fields_headers"] = fields_headers;
            ps["group_headers"] = group_headers;
            ps["is_group_headers"] = is_group_headers;
            ps["f"] = f;
            ps["defs"] = defs;
            ps["d"] = dict;
            ps["is_readonly"] = is_readonly;

            return ps;
        }

        public Hashtable ShowFormAction(string form_id = "")
        {
            if (is_readonly)
                throw new UserException("Access denied");

            check_dict();

            Hashtable hf = new();
            Hashtable item;
            int id = Utils.f2int(form_id);
            ArrayList cols = model_tables.getColumns(defs);
            bool is_fwtable = false;

            if (string.IsNullOrEmpty((string)defs["list_columns"]))
            {
                // if no custom columns - remove sys cols
                is_fwtable = true;
                cols = model.filterOutSysCols(cols);
            }


            if (isGet())
            {
                if (id > 0)
                    item = model.oneByTname(dict, id);
                else
                {
                    // set defaults here
                    item = new();
                    // item["field"]="default value";
                    item["prio"] = model.maxIdByTname(dict) + 1; // default prio (if exists) = max(id)+1 
                }
            }
            else
            {
                // read from db
                item = model.oneByTname(dict, id);
                // and merge new values from the form
                Utils.mergeHash(item, reqh("item"));
            }

            ArrayList fv = new();
            string last_igroup = "";
            foreach (Hashtable col in cols)
            {
                if (is_fwtable && (string)col["name"] == "status")
                    continue; // for fw tables - status displayed in standard way

                Hashtable fh = new();
                fh["colname"] = col["name"];
                fh["iname"] = col["iname"];
                fh["value"] = item[col["name"]];
                fh["type"] = col["itype"].ToString().Trim();
                if (!string.IsNullOrEmpty((string)col["maxlen"]))
                {
                    if ((string)col["maxlen"] == "-1")
                    {
                        fh["maxlen"] = ""; // textarea
                        fh["type"] = "textarea";
                    }
                    else
                        fh["maxlen"] = col["maxlen"];
                }
                else
                    fh["maxlen"] = col["numeric_precision"];
                if (col["itype"].ToString().Contains("."))
                {
                    // lookup type
                    fh["type"] = "lookup";
                    fh["select_options"] = model_tables.getLookupSelectOptions((string)col["itype"], fh["value"]);
                }

                string igroup = col["igroup"].ToString().Trim();
                if (igroup != last_igroup)
                {
                    fh["is_group"] = true;
                    fh["igroup"] = igroup;
                    last_igroup = igroup;
                }

                fv.Add(fh);
            }
            hf["fields"] = fv;

            hf["is_fwtable"] = is_fwtable;
            if (is_fwtable)
            {
                hf["add_users_id_name"] = fw.model<Users>().iname(item["add_users_id"]);
                hf["upd_users_id_name"] = fw.model<Users>().iname(item["upd_users_id"]);
            }

            hf["id"] = id;
            hf["i"] = item;
            hf["defs"] = defs;
            hf["d"] = dict;

            return hf;
        }

        public void SaveAction(string form_id = "")
        {
            if (is_readonly)
                throw new UserException("Access denied");

            check_dict();

            Hashtable item = reqh("item");
            int id = Utils.f2int(form_id);
            ArrayList cols = model_tables.getColumns(defs);

            try
            {
                Validate(id, item);

                Hashtable itemdb = new();
                foreach (Hashtable col in cols)
                {
                    if (item.ContainsKey(col["name"]))
                        itemdb[col["name"]] = item[col["name"]];
                    else if ((string)col["itype"] == "checkbox")
                        itemdb[col["name"]] = 0;// for checkboxes just set them 0
                }

                if (id > 0)
                {
                    if (model.updateByTname(dict, id, itemdb))
                        fw.flash("updated", 1);
                }
                else
                {
                    model.addByTname(dict, itemdb);
                    fw.flash("added", 1);
                }

                // redirect to list as we don't have id on insert
                // fw.redirect(base_url + "/" + id + "/edit")
                fw.redirect(base_url + "/?d=" + dict);
            }
            catch (ApplicationException ex)
            {
                fw.G["err_msg"] = ex.Message;
                String[] args = new[] { id.ToString() };
                fw.routeRedirect("ShowForm", null, args);
            }
        }

        public bool Validate(int id, Hashtable item)
        {
            bool result = true;
            result &= validateRequired(item, Utils.qw(required_fields));
            if (!result)
                fw.FERR["REQ"] = 1;

            if (fw.FERR.Count > 0 && !fw.FERR.ContainsKey("REQ"))
                fw.FERR["INVALID"] = 1;

            if (!result)
                throw new ApplicationException("");
            return true;
        }

        public Hashtable ShowDeleteAction(string form_id)
        {
            if (is_readonly)
                throw new UserException("Access denied");

            check_dict();

            Hashtable hf = new();
            int id = Utils.f2int(form_id);
            Hashtable item = model.oneByTname(dict, id);
            hf["i"] = item;
            hf["iname"] = item[new ArrayList(item.Keys)[0]];
            hf["id"] = id;
            hf["defs"] = defs;
            hf["d"] = dict;

            return hf;
        }

        public void DeleteAction(string form_id)
        {
            if (is_readonly)
                throw new UserException("Access denied");

            check_dict();
            int id = Utils.f2int(form_id);

            model.deleteByTname(dict, id);
            fw.flash("onedelete", 1);
            fw.redirect(base_url + "/?d=" + dict);
        }

        public void SaveMultiAction()
        {
            if (is_readonly)
                throw new UserException("Access denied");

            check_dict();

            try
            {
                int del_ctr = 0;
                Hashtable cbses = reqh("cb");
                if (cbses == null)
                    cbses = new Hashtable();
                if (cbses.Count > 0)
                {
                    // multirecord delete
                    foreach (string id in cbses.Keys)
                    {
                        if (fw.FORM.ContainsKey("delete"))
                        {
                            model.deleteByTname(dict, Utils.f2int(id));
                            del_ctr += 1;
                        }
                    }
                }

                if (reqs("mode") == "edit")
                {
                    // multirecord save
                    ArrayList cols = model_tables.getColumns(defs);

                    // go thru all existing rows
                    Hashtable rows = reqh("row");
                    if (rows == null)
                        rows = new Hashtable();
                    Hashtable rowsdel = reqh("del");
                    if (rowsdel == null)
                        rowsdel = new Hashtable();
                    Hashtable ids_md5 = new();
                    foreach (string key in rows.Keys)
                    {
                        string form_id = key;
                        int id = Utils.f2int(form_id);
                        if (id == 0)
                            continue; // skip wrong rows

                        string md5 = (string)rows[key];
                        // logger(form_id)
                        Hashtable item = reqh("f" + form_id);
                        Hashtable itemdb = new();
                        // copy from form item to db item - only defined columns
                        foreach (Hashtable col in cols)
                        {
                            if (item.ContainsKey(col["name"]))
                                itemdb[col["name"]] = item[col["name"]];
                        }
                        // check if this row need to be deleted
                        if (rowsdel.ContainsKey(form_id))
                        {
                            model.deleteByTname(dict, id);
                            del_ctr += 1;
                        }
                        else
                        {
                            // existing row
                            model.updateByTname(dict, id, itemdb, md5);
                            fw.flash("updated", 1);
                        }
                    }

                    // new rows
                    rows = reqh("new");
                    foreach (string key in rows.Keys)
                    {
                        string form_id = key;
                        int id = Utils.f2int(form_id);
                        if (id == 0)
                            continue; // skip wrong rows
                                      // logger("new formid=" & form_id)

                        Hashtable item = reqh("fnew" + form_id);
                        Hashtable itemdb = new();
                        bool is_row_empty = true;
                        // copy from form item to db item - only defined columns
                        foreach (Hashtable col in cols)
                        {
                            if (item.ContainsKey(col["name"]))
                            {
                                itemdb[col["name"]] = item[col["name"]];
                                if (!string.IsNullOrEmpty((string)item[col["name"]]))
                                    is_row_empty = false; // detect at least one non-empty value
                            }
                        }

                        // add new row, but only if at least one value is not empty
                        if (!is_row_empty)
                        {
                            model.addByTname(dict, itemdb);
                            fw.flash("updated", 1);
                        }
                    }
                }

                if (del_ctr > 0)
                    fw.flash("multidelete", del_ctr);

                fw.redirect(base_url + "/?d=" + dict);
            }
            catch (Exception ex)
            {
                throw;
                fw.G["err_msg"] = ex.Message;
                fw.routeRedirect("Index");
            }
        }

        // TODO for lookup tables
        public Hashtable AutocompleteAction()
        {
            ArrayList items = model_tables.getAutocompleteList(reqs("q"));

            return new Hashtable() { { "_json", items } };
        }
    }
}