using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using radioTranscodeManager.Services;

namespace audioConverter.Services
{
    public class StationDB : DbContext
    {
        private static StationDB _db;
        private static string _dbPath;

        public DbSet<OutputRadioStationConverter> RadioSta { get; set; }


        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source={_dbPath}");

        public StationDB()
        {
            if (_db == null)
            {
                _db = new StationDB();
                var folder = Environment.SpecialFolder.LocalApplicationData;
                var path = Environment.GetFolderPath(folder);
                _dbPath = System.IO.Path.Join(path, "radio_station.db");
            }
        }


        public List<OutputRadioStationConverter> GetAllItemsInDb()
        {
            var listItems = new List<OutputRadioStationConverter>();
            return listItems;
        }
        public static Boolean WriteNewItemToDb(OutputRadioStationConverter newItem)
        {
            bool retval = false;
            try
            {
                _db.Add(newItem);
                _db.SaveChanges();
                retval = true;
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"Save item to db failed {ex.ToString()}");
            }
            return retval;
        }


        public static Boolean EditItemInDb(OutputRadioStationConverter newItem)
        {
            bool retval = false;
            try
            {
                var item = _db.RadioSta
                            .OrderBy(b => b.StationName)
                            .First();
                if (item != null)
                {
                    item = newItem;
                    _db.SaveChanges();
                    retval = true;
                }
                else
                {
                    Console.WriteLine($"Edit item in db failed : {item.StationName}");
                }
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"Edit item in db failed {ex.ToString()}");
            }
            return retval;
        }

        public static OutputRadioStationConverter ReadItemInDb(string name)
        {
            bool retval = false;
            try
            {
                var item = _db.RadioSta
                            .OrderBy(b => b.StationName)
                            .First();
                if (item != null)
                {
                    item = newItem;
                    _db.SaveChanges();
                    retval = true;
                }
                else
                {
                    Console.WriteLine($"Edit item in db failed : {item.StationName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Edit item in db failed {ex.ToString()}");
            }
            return retval;
        }

        public static bool DeleteItemInDb(OutputRadioStationConverter newItem)
        {
            Boolean retval = false;
            try
            {
                var item = _db.RadioSta
                            .OrderBy(b => b.StationName)
                            .First();
                if (item != null)
                {
                    _db.Remove(item);
                    _db.SaveChanges();
                }
                else
                {
                    Console.WriteLine($"Delete item in db : {item.StationName} : OK, item not existed");
                }
                retval = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Edit item in db failed {ex.ToString()}");
            }
            return retval;
        }
    }
}
