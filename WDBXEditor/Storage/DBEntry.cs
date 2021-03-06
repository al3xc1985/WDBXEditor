﻿using WDBXEditor.Common;
using WDBXEditor.Reader;
using MySql.Data.MySqlClient;
using WDBXEditor.Archives.MPQ;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using static WDBXEditor.Common.Constants;
using static WDBXEditor.Forms.InputBox;
using WDBXEditor.Reader.FileTypes;
using System.Text.RegularExpressions;
using static WDBXEditor.Common.Extensions;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Web.Script.Serialization;

namespace WDBXEditor.Storage
{
    public class DBEntry : IDisposable
    {
        public DBHeader Header { get; private set; }
        public DataTable Data { get; set; }
        public bool Changed { get; set; } = false;
        public string FilePath { get; private set; }
        public string FileName => Path.GetFileName(this.FilePath);
        public string SavePath { get; set; }
        public Table TableStructure { get; private set; }

        public string Key { get; private set; }
        public int Build { get; private set; }
        public string BuildName { get; private set; }
        public string Tag { get; private set; }

        public DBEntry(DBHeader header, string filepath)
        {
            this.Header = header;
            this.FilePath = filepath;
            this.SavePath = filepath;
            this.TableStructure = Database.Definitions.Tables.FirstOrDefault(x =>
                                          x.Name.Equals(Path.GetFileNameWithoutExtension(filepath), IGNORECASE) &&
                                          x.Build == Database.BuildNumber);

            LoadDefinition();
        }


        /// <summary>
        /// Converts the XML definition to an empty DataTable
        /// </summary>
        public void LoadDefinition()
        {
            if (TableStructure == null)
                return;

            Build = TableStructure.Build;
            Key = TableStructure.Key.Name;
            BuildName = BuildText(Build);
            Tag = Guid.NewGuid().ToString();

            //Column name check
            if (TableStructure.Fields.GroupBy(x => x.Name).Any(y => y.Count() > 1))
            {
                MessageBox.Show($"Duplicate column names for {FileName} - {Build} definition");
                return;
            }

            Data = new DataTable() { TableName = "Data", CaseSensitive = false };

            var LocalizationCount = (Build <= (int)ExpansionFinalBuild.Classic ? 9 : 17); //Pre TBC had 9 locales

            foreach (var col in TableStructure.Fields)
            {
                Queue<TextWowEnum> languages = new Queue<TextWowEnum>(Enum.GetValues(typeof(TextWowEnum)).Cast<TextWowEnum>());

                for (int i = 0; i < col.ArraySize; i++)
                {
                    string columnName = col.Name;
                    if (col.ArraySize > 1)
                        columnName += (i > 0 ? "_" + i.ToString() : "");

                    switch (col.Type.ToLower())
                    {
                        case "sbyte":
                            Data.Columns.Add(columnName, typeof(sbyte));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "byte":
                            Data.Columns.Add(columnName, typeof(byte));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "int32":
                        case "int":
                            Data.Columns.Add(columnName, typeof(int));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "uint32":
                        case "uint":
                            Data.Columns.Add(columnName, typeof(uint));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "int64":
                        case "long":
                            Data.Columns.Add(columnName, typeof(long));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "uint64":
                        case "ulong":
                            Data.Columns.Add(columnName, typeof(ulong));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "single":
                        case "float":
                            Data.Columns.Add(columnName, typeof(float));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "boolean":
                        case "bool":
                            Data.Columns.Add(columnName, typeof(bool));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "string":
                            Data.Columns.Add(columnName, typeof(string));
                            Data.Columns[columnName].DefaultValue = string.Empty;
                            break;
                        case "int16":
                        case "short":
                            Data.Columns.Add(columnName, typeof(short));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "uint16":
                        case "ushort":
                            Data.Columns.Add(columnName, typeof(ushort));
                            Data.Columns[columnName].DefaultValue = 0;
                            break;
                        case "loc": 
                            //Special case for localized strings, build up all locales and add string mask
                            for (int x = 0; x < LocalizationCount; x++)
                            {
                                if (x == LocalizationCount - 1)
                                {
                                    Data.Columns.Add(col.Name + "_Mask", typeof(uint)); //Last column is a mask
                                    Data.Columns[col.Name + "_Mask"].AllowDBNull = false;
                                    Data.Columns[col.Name + "_Mask"].DefaultValue = 0;
                                }
                                else
                                {
                                    columnName = col.Name + "_" + languages.Dequeue().ToString(); //X columns for local strings
                                    Data.Columns.Add(columnName, typeof(string));
                                    Data.Columns[columnName].AllowDBNull = false;
                                    Data.Columns[columnName].DefaultValue = string.Empty;
                                    TableStructure.Padding.Add(0);
                                }
                            }
                            break;
                        default:
                            throw new Exception($"Unknown field type {col.Type} for {col.Name}.");
                    }

                    //AutoGenerated Id for CharBaseInfo
                    if (col.AutoGenerate)
                    {
                        Data.Columns[0].ExtendedProperties.Add(AUTO_GENERATED, true);
                        Header.AutoGeneratedColumns++;
                    }

                    Data.Columns[columnName].AllowDBNull = false;
                    TableStructure.Padding.Add(col.Padding);
                }
            }

            //Setup the Primary Key
            Data.Columns[Key].DefaultValue = null; //Clear default value
            Data.PrimaryKey = new DataColumn[] { Data.Columns[Key] };
            Data.Columns[Key].AutoIncrement = true;
            Data.Columns[Key].Unique = true;
        }

        /// <summary>
        /// Gets the Min and Max ids
        /// </summary>
        /// <returns></returns>
        public Tuple<int, int> MinMax()
        {
            int min = int.MaxValue;
            int max = int.MinValue;
            foreach (DataRow dr in Data.Rows)
            {
                int val = dr.Field<int>(Key);
                min = Math.Min(min, val);
                max = Math.Max(max, val);
            }
            return new Tuple<int, int>(min, max);
        }

        /// <summary>
        /// Checks if the file is of Name and Expansion
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="expansion"></param>
        /// <returns></returns>
        /// 
        public bool IsFileOf(string filename, Expansion expansion)
        {
            return TableStructure.Name.Equals(filename, IGNORECASE) && IsBuild(Build, expansion);
        }

        public bool IsFileOf(string filename)
        {
            return TableStructure.Name.Equals(filename, IGNORECASE);
        }

        /// <summary>
        /// Generates a Bit map for all columns as the Blizzard one combines array columns
        /// </summary>
        /// <returns></returns>
        public FieldStructureEntry[] GetBits()
        {
            FieldStructureEntry[] bits = new FieldStructureEntry[Data.Columns.Count];
            if (!Header.IsTypeOf<WDB5>())
                return bits;

            int c = 0;
            for (int i = 0; i < TableStructure.Fields.Count; i++)
            {
                Field f = TableStructure.Fields[i];
                for (int x = 0; x < f.ArraySize; x++)
                {
                    bits[c] = new FieldStructureEntry(0, 0);
                    bits[c].Bits = ((WDB5)Header).FieldStructure[i]?.Bits ?? 0;
                    c++;
                }
            }

            return bits;
        }


        #region Special Data
        /// <summary>
        /// Gets a list of Ids
        /// </summary>
        /// <returns></returns>
        public IEnumerable<int> GetPrimaryKeys()
        {
            for (int i = 0; i < Data.Rows.Count; i++)
                yield return Data.Rows[i].Field<int>(Key);
        }

        /// <summary>
        /// Produces a list of unique rows (excludes key values)
        /// </summary>
        /// <returns></returns>
        public IEnumerable<DataRow> GetUniqueRows()
        {
            DBRowComparer comparer = new DBRowComparer(Data.Columns.IndexOf(Key));
            return Data.AsEnumerable().Distinct(comparer);
        }

        /// <summary>
        /// Generates a map of unqiue rows and grouped count
        /// </summary>
        /// <returns></returns>
        public IEnumerable<dynamic> GetCopyRows()
        {
            DBRowComparer comparer = new DBRowComparer(Data.Columns.IndexOf(Key));

            return Data.AsEnumerable().GroupBy(r => r, comparer)
                                      .Where(g => g.Count() > 1)
                                      .Select(g => new
                                      {
                                          Key = g.Key,
                                          Copies = g.Where(r => r != g.Key).Select(r => r.Field<int>(comparer.IdColumnIndex)).ToArray()
                                      });
        }

        /// <summary>
        /// Extracts the id and the total length of strings for each row
        /// </summary>
        /// <returns></returns>
        public Dictionary<int, short> GetStringLengths()
        {
            Dictionary<int, short> result = new Dictionary<int, short>();
            IEnumerable<string> cols = Data.Columns.Cast<DataColumn>()
                                              .Where(x => x.DataType == typeof(string))
                                              .Select(x => x.ColumnName);

            foreach (DataRow row in Data.Rows)
            {
                short total = 0;
                foreach (string c in cols)
                {
                    short len = (short)Encoding.UTF8.GetByteCount(row[c].ToString());
                    total += (short)(len > 0 ? len + 1 : 0);
                }
                result.Add(row.Field<int>(Key), total);
            }

            return result;
        }
        #endregion


        #region Exports
        /// <summary>
        /// Generates a SQL string to DROP and ADD a table then INSERT the records
        /// </summary>
        /// <returns></returns>
        public string ToSQL()
        {
            string tableName = $"db_{TableStructure.Name}_{Build}";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"DROP TABLE IF EXISTS `{tableName}`; ");
            sb.AppendLine($"CREATE TABLE `{tableName}` ({Data.Columns.ToSql(Key)}) ENGINE=MyISAM DEFAULT CHARSET=utf8; ");
            foreach (DataRow row in Data.Rows)
                sb.AppendLine($"INSERT INTO `{tableName}` VALUES ({ row.ToSql() }); ");

            return sb.ToString();
        }

        /// <summary>
        /// Uses MysqlBulkCopy to import the data directly into a database
        /// </summary>
        /// <param name="connectionstring"></param>
        public void ToSQLTable(string connectionstring)
        {
            string tableName = $"db_{TableStructure.Name}_{Build}";
            string csvName = Path.Combine(TEMP_FOLDER, tableName + ".csv");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION';");
            sb.AppendLine($"DROP TABLE IF EXISTS `{tableName}`; ");
            sb.AppendLine($"CREATE TABLE `{tableName}` ({Data.Columns.ToSql(Key)}) ENGINE=MyISAM DEFAULT CHARACTER SET = utf8 COLLATE = utf8_unicode_ci; ");

            using (StreamWriter csv = new StreamWriter(csvName))
                csv.Write(ToCSV());

            using (MySqlConnection connection = new MySqlConnection(connectionstring))
            {
                connection.Open();

                using (MySqlCommand command = new MySqlCommand(sb.ToString(), connection))
                    command.ExecuteNonQuery();

                new MySqlBulkLoader(connection)
                {
                    TableName = $"`{tableName}`",
                    FieldTerminator = ",",
                    LineTerminator = "\r\n",
                    NumberOfLinesToSkip = 1,
                    FileName = csvName,
                    FieldQuotationCharacter = '"',
                    CharacterSet = "UTF8"
                }.Load();
            }

            try { File.Delete(csvName); }
            catch { }
        }

        /// <summary>
        /// Generates a CSV file string
        /// </summary>
        /// <returns></returns>
        public string ToCSV()
        {
            StringBuilder sb = new StringBuilder();
            IEnumerable<string> columnNames = Data.Columns.Cast<DataColumn>().Select(column => column.ColumnName);
            sb.AppendLine(string.Join(",", columnNames));

            Func<string, string> EncodeCsv = s => { return string.Concat("\"", s.Replace(Environment.NewLine, string.Empty).Replace("\"", "\"\""), "\""); };

            foreach (DataRow row in Data.Rows)
            {
                IEnumerable<string> fields = row.ItemArray.Select(field => EncodeCsv(field.ToString()));
                sb.AppendLine(string.Join(",", fields));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Appends to or creates a MPQ file
        /// <para>Picks the appropiate version based on the build number.</para>
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="version"></param>
        public void ToMPQ(string filename)
        {
            MpqArchiveVersion version = MpqArchiveVersion.Version2;
            if (this.Build <= (int)ExpansionFinalBuild.WotLK)
                version = MpqArchiveVersion.Version2;
            else if (this.Build <= (int)ExpansionFinalBuild.MoP)
                version = MpqArchiveVersion.Version4;
            else
            {
                MessageBox.Show("Only clients before WoD support MPQ archives.");
                return;
            }

            try
            {
                MpqArchive archive = null;
                if (File.Exists(filename))
                {
                    switch (ShowOverwriteDialog("You've selected an existing MPQ archive.\r\nWhich action would you like to take?", "Existing MPQ"))
                    {
                        case DialogResult.Yes: //Append
                            archive = new MpqArchive(filename, FileAccess.Write);
                            break;
                        case DialogResult.No: //Overwrite
                            archive = MpqArchive.CreateNew(filename, version);
                            break;
                        default:
                            return;
                    }
                }
                else
                    archive = MpqArchive.CreateNew(filename, version);

                string tmpPath = Path.Combine(TEMP_FOLDER, TableStructure.Name);
                string fileName = Path.GetFileName(FilePath);
                string filePath = Path.Combine("DBFilesClient", fileName);

                new DBReader().Write(this, tmpPath);
                archive.AddFileFromDisk(tmpPath, filePath);

                int retval = archive.AddListFile(filePath);
                archive.Compact(filePath);
                archive.Flush();
                archive.Dispose();
            } //Save the file
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting to MPQ archive {ex.Message}");
            }
        }

        /// <summary>
        /// Generates a JSON string
        /// </summary>
        /// <returns></returns>
        public string ToJSON()
        {
            string[] columns = Data.Columns.Cast<DataColumn>().Select(x => x.ColumnName).ToArray();
            ConcurrentBag<Dictionary<string, object>> Rows = new ConcurrentBag<Dictionary<string, object>>();
            Parallel.For(0, Data.Rows.Count, r =>
            {
                object[] data = Data.Rows[r].ItemArray;

                Dictionary<string, object> row = new Dictionary<string, object>();
                for (int x = 0; x < columns.Length; x++)
                    row.Add(columns[x], data[x]);

                Rows.Add(row);
            });
            
            return new JavaScriptSerializer() { MaxJsonLength = int.MaxValue }.Serialize(Rows);
        }

        #endregion


        #region Imports
        public bool ImportCSV(string filename, bool headerrow, UpdateMode mode, out string error, ImportFlags flags)
        {
            error = string.Empty;

            DataTable importTable = Data.Clone(); //Clone table structure to help with mapping

            List<int> usedids = new List<int>();
            int idcolumn = Data.Columns[Key].Ordinal;
            int maxid = int.MinValue;

            string pathOnly = Path.GetDirectoryName(filename);
            string fileName = Path.GetFileName(filename);

            Func<string, string> Unescape = s =>
            {
                if (s.StartsWith("\"") && s.EndsWith("\""))
                {
                    s = s.Substring(1, s.Length - 2);
                    if (s.Contains("\"\""))
                        s = s.Replace("\"\"", "\"");
                }
                return s;
            };

            try
            {
                using (StreamReader sr = new StreamReader(File.OpenRead(filename)))
                {
                    if (headerrow)
                        sr.ReadLine();

                    while (!sr.EndOfStream)
                    {
                        string line = sr.ReadLine();
                        string[] rows = Regex.Split(line, ",(?=(?:[^\"]*\"[^\"]*\")*(?![^\"]*\"))", RegexOptions.Compiled);
                        DataRow dr = importTable.NewRow();

                        for (int i = 0; i < Data.Columns.Count; i++)
                        {
                            string value = Unescape(rows[i]);

                            switch (Data.Columns[i].DataType.Name.ToLower())
                            {
                                case "sbyte":
                                    dr[i] = Convert.ToSByte(value);
                                    break;
                                case "byte":
                                    dr[i] = Convert.ToByte(value);
                                    break;
                                case "int32":
                                case "int":
                                    dr[i] = Convert.ToInt32(value);
                                    break;
                                case "uint32":
                                case "uint":
                                    dr[i] = Convert.ToUInt32(value);
                                    break;
                                case "int64":
                                case "long":
                                    dr[i] = Convert.ToInt64(value);
                                    break;
                                case "uint64":
                                case "ulong":
                                    dr[i] = Convert.ToUInt64(value);
                                    break;
                                case "single":
                                case "float":
                                    dr[i] = Convert.ToSingle(value);
                                    break;
                                case "boolean":
                                case "bool":
                                    dr[i] = Convert.ToBoolean(value);
                                    break;
                                case "string":
                                    dr[i] = value;
                                    break;
                                case "int16":
                                case "short":
                                    dr[i] = Convert.ToInt16(value);
                                    break;
                                case "uint16":
                                case "ushort":
                                    dr[i] = Convert.ToUInt16(value);
                                    break;
                            }

                            //Double check our Ids
                            if (i == idcolumn)
                            {
                                int id = (int)dr[i];

                                if (flags.HasFlag(ImportFlags.TakeNewest) && usedids.Contains(id))
                                {
                                    var prev = importTable.Rows.Find(id);
                                    if (prev != null)
                                        importTable.Rows.Remove(prev);
                                }
                                else if (flags.HasFlag(ImportFlags.FixIds) && usedids.Contains(id))
                                {
                                    dr[i] = ++maxid;
                                    id = (int)dr[i];
                                }

                                usedids.Add(id); //Add to list
                                maxid = Math.Max(maxid, id); //Update maxid
                            }
                        }

                        importTable.Rows.Add(dr);
                    }
                }
            }
            catch (FormatException ex)
            {
                error = $"Mismatch of data to datatype in row index {usedids.Count + 1}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            switch (Data.ShallowCompare(importTable, false))
            {
                case CompareResult.Type:
                    error = "Import Failed: Imported data has one or more incorrect column types.";
                    return false;
                case CompareResult.Count:
                    error = "Import Failed: Imported data has an incorrect number of columns.";
                    return false;
            }

            UpdateData(importTable, mode);
            return true;
        }

        public bool ImportSQL(UpdateMode mode, string connectionstring, string table, out string error, string columns = "*")
        {
            error = string.Empty;
            DataTable importTable = Data.Clone(); //Clone table structure to help with mapping
            Parallel.For(0, importTable.Columns.Count, c => importTable.Columns[c].AllowDBNull = true); //Allow null values

            using (MySqlConnection connection = new MySqlConnection(connectionstring))
            using (MySqlCommand command = new MySqlCommand($"SELECT {columns} FROM `{table}`", connection))
            using (MySqlDataAdapter adapter = new MySqlDataAdapter(command))
            {
                try
                {
                    adapter.FillSchema(importTable, SchemaType.Source); //Enforce schema
                    adapter.Fill(importTable);
                }
                catch(ConstraintException ex)
                {
                    error = ex.Message;
                    return false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                    return false;
                }
            }

            //Replace DBNulls with default value
            var defaultVals = importTable.Columns.Cast<DataColumn>().Select(x => x.DefaultValue).ToArray();
            Parallel.For(0, importTable.Rows.Count, r =>
            {
                for (int i = 0; i < importTable.Columns.Count; i++)
                    if (importTable.Rows[r][i] == DBNull.Value)
                        importTable.Rows[r][i] = defaultVals[i];
            });

            switch (Data.ShallowCompare(importTable))
            {
                case CompareResult.DBNull:
                    error = "Import Failed: Imported data contains NULL values.";
                    return false;
                case CompareResult.Type:
                    error = "Import Failed: Imported data has incorrect column types.";
                    return false;
                case CompareResult.Count:
                    error = "Import Failed: Imported data has an incorrect number of columns.";
                    return false;
            }

            UpdateData(importTable, mode);
            return true;
        }

        private void UpdateData(DataTable importTable, UpdateMode mode)
        {
            switch (mode)
            {
                case UpdateMode.Insert:
                    //Insert all rows where the ID doesn't already exist already into the existing datatable
                    var rows = Data.Except(importTable, Key);
                    var source = Data.Copy();

                    source.BeginLoadData();
                    foreach (var r in rows)
                        source.Rows.Add(r);
                    source.EndLoadData();

                    Data.Clear();
                    Data = source;

                    break;

                case UpdateMode.Replace:
                    //Simply change the datatable
                    Data = importTable.Copy();
                    break;

                case UpdateMode.Update:
                    //Insert all the missing existing rows into the new dataset then change the datatable
                    var rows2 = importTable.Except(Data, Key);

                    importTable.BeginLoadData();
                    foreach (var r in rows2)
                        importTable.Rows.Add(r);
                    importTable.EndLoadData();

                    Data = importTable.Copy();
                    break;
            }

            Parallel.For(0, Data.Columns.Count, c => Data.Columns[c].AllowDBNull = false); //Disallow null values

            importTable.Clear();
            importTable.Dispose();
            Database.ForceGC();
        }

        #endregion


        public void Dispose()
        {
            this.Data.Reset();
            ((IDisposable)this.Data).Dispose();
        }
    }
}
