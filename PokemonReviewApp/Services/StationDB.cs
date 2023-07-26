using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileSystemGlobbing.Internal;
using Microsoft.Extensions.Hosting;
using radioTranscodeManager.Services;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Xml.Linq;
using audioConverter.Services;
using Serilog;

namespace audioConverter.Services
{
    public class StationDB : DbContext
    {
        private static StationDB ?_db = null;
        private static string _dbPath = String.Empty;
        private static string _dataSourceToDbPath = String.Empty;
        private const string DB_PATH = "./radio_station.db";
        private static List<OutputRadioStationConverter> _cachedListStation = new List<OutputRadioStationConverter>();
        private static object _ensureThreadSafe = new Object();
        private const string DB_TABLE_NAME = "StationRecord";

        private static OutputRadioStationConverter Clone(OutputRadioStationConverter obj)
        {
            var cloned = new OutputRadioStationConverter
            {
                OutputUrl = obj.OutputUrl,
                InputUrl = obj.InputUrl,
                Description = obj.Description,
                StationName = obj.StationName,
            };
            return cloned;
        }

        private static SQLiteConnection SimpleDbConnection()
        {
            if (_db == null)
            {
                _db = new StationDB();
                _dbPath = DB_PATH;  /*System.IO.Path.Join(path, "radio_station2.db");*/
                _dataSourceToDbPath = "Data Source=" + _dbPath;
                Log.Information($"DB path = {_dbPath}");
            }
            return new SQLiteConnection(_dataSourceToDbPath);
        }

        public static void InitDataBase()
        {
            using (var cnn = SimpleDbConnection())
            {
                if (cnn != null && !File.Exists(_dbPath))
                {
                    Log.Information("Create new database");
                    try
                    {
                        string cmd = $@"Create Table {DB_TABLE_NAME}
                        (
                            StationName                         TEXT,
                            Description                         TEXT,
                            InputUrl                            TEXT,
                            OutputUrl                           TEXT,
                            PRIMARY KEY(StationName)
                        )";

                        cnn.Open();
                        cnn.Execute(cmd);
                    }
                    catch (Exception ex) 
                    {
                        Log.Warning($"Create db failed {ex.Message}");
                    }
                }   
                else if (File.Exists(_dbPath) && cnn == null)
                {
                    Log.Warning($"Connect to db {_dbPath} failed");
                }
                else
                {
                    Log.Information("Connected to db");
                }

                lock (_ensureThreadSafe)
                {
                    var tmp = GetAllItemsInDb();
                    if (tmp != null)
                    {
                        _cachedListStation = tmp;
                    }
                }
            }
        }


        public static List<OutputRadioStationConverter> ? GetAllItemsInDb()
        {
            try
            {
                var cnn = SimpleDbConnection();
                string cmd = $"select * from {DB_TABLE_NAME}";
                var output = cnn.Query<OutputRadioStationConverter>(cmd, new DynamicParameters());
                return output.ToList();
            }
            catch(Exception ex)
            {
                Log.Warning($"Get all item in db failed {ex.Message}");
                return null;
            }
        }

        public static Boolean WriteNewItemToDb(OutputRadioStationConverter newItem)
        {
            //TODO add thread safe
            bool retval = false;
            bool isExited = false;
            lock (_ensureThreadSafe)
            {
                for (int i = 0; i < _cachedListStation.Count; i++)
                {
                    if (_cachedListStation[i].StationName.Equals(newItem.StationName))
                    {
                        Log.Information("Item already existed");
                        isExited = true;
                        break;
                    }
                }

                if (!isExited)
                {
                    _cachedListStation.Add(newItem);
                    try
                    {
                        var cnn = SimpleDbConnection();
                        cnn.Execute("insert into StationRecord (StationName, Description, InputUrl, OutputUrl) values (@StationName, @Description, @InputUrl, @OutputUrl)",
                                    newItem);
                        _cachedListStation.Add(newItem);
                        retval = true;
                    }
                    catch (Exception ex) { Log.Warning($"Insert to db failed {ex.Message}"); }
                }
                else
                {
                    retval = true;
                }
            }
            return retval;
        }


        public static Boolean EditItemInDb(string oldName, OutputRadioStationConverter newItem)
        {
            bool retval = false;
            int index = -1;
            lock (_ensureThreadSafe)
            {
                for (int i = 0; i < _cachedListStation.Count; i++)
                {
                    if (_cachedListStation[i].StationName.Equals(oldName))
                    {
                        Log.Information("Item already existed");
                        index = i;
                        break;
                    }
                }

                if (index != -1)
                {
                    try
                    {
                        var cnn = SimpleDbConnection();
                        cnn.Execute($"DELETE FROM {DB_TABLE_NAME} WHERE StationName = '{oldName}'");
                        cnn.Execute("insert into StationRecord (StationName, Description, InputUrl, OutputUrl) values (@StationName, @Description, @InputUrl, @OutputUrl)",
                                    newItem);

                        _cachedListStation[index] = newItem;
                        retval = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Edit item in db failed {ex.ToString()}");
                    }
                }
                else
                {
                    Log.Information("Item not existed\r\n");
                }
            }
            return retval;
        }

        public static OutputRadioStationConverter? ReadItemInDbByName(string name)
        {
            int index = -1;
            OutputRadioStationConverter? tmp = null;
            lock (_ensureThreadSafe)
            {
                for (int i = 0; i < _cachedListStation.Count; i++)
                {
                    if (_cachedListStation[i].StationName.Equals(name))
                    {
                        Log.Information("Item already existed");
                        index = i;
                        break;
                    }
                }

                if (index != -1)
                {
                    tmp = Clone(_cachedListStation[index]);
                }
            }
            return tmp;
        }

        public static bool RemoveItemInDb(string name)
        {
            Boolean retval = false;
            int index = -1;
            lock (_ensureThreadSafe) 
            {
                for (int i = 0; i < _cachedListStation.Count; i++)
                {
                    if (_cachedListStation[i].StationName.Equals(name))
                    {
                        Log.Information($"Item {name} already existed");
                        index = i;
                        break;
                    }
                }

                if (index != -1)
                {
                    Log.Information($"Remove item with name {name}");
                    try
                    {
                        var cnn = SimpleDbConnection();
                        cnn.Execute(String.Format($"DELETE FROM StationRecord WHERE StationName = '{name}'"));
                        _cachedListStation.RemoveAt(index);
                        retval = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Edit item in db failed {ex.ToString()}");
                    }
                }
                else
                {
                    Log.Warning($"Item {name} not existed");
                }
            }
            return retval;
        }
    }
}
