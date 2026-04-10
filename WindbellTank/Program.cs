using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Data.SqlClient;

namespace WindbellTest
{
    public class ErrorData
    {
        public string code { get; set; }
        public string message { get; set; }
    }

    public class TankData
    {
        public int tank_id { get; set; }
        public string product_code { get; set; }
        public double? oil_level { get; set; }
        public double? water_level { get; set; }
        public double? temperature { get; set; }
        public double? volume { get; set; }
        public double? water_volume { get; set; }
        public double? tc_volume { get; set; }
        public double? capacity { get; set; }
        public double? Ullage { get; set; }
        public string sensor_status { get; set; }
        public ErrorData error { get; set; }
    }

    public class AtgMetadata
    {
        public string request_id { get; set; }
        public string timestamp { get; set; }
    }

    public class AtgResponse
    {
        public bool success { get; set; }
        public AtgMetadata metadata { get; set; }
        public List<TankData> data { get; set; }
    }

    class Program
    {
        static string GetConnectionString()
        {
            string machineName = Environment.MachineName;
            return $"Server={machineName};Database=ofisServer;User Id=sa;Password=374474;Encrypt=False;";
        }

        static string GetIpFromDatabase()
        {
            try
            {
                using (var conn = new SqlConnection(GetConnectionString()))
                {
                    conn.Open();
                    using (var cmd = new SqlCommand("SELECT TOP 1 ip FROM TankConfig WHERE len(isnull(ip, '')) > 0", conn))
                    {
                        var res = cmd.ExecuteScalar();
                        if (res != null && res != DBNull.Value)
                        {
                            return res.ToString().Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[\u26A0] Verilənlər bazasından məlumat oxunarkən xəta: {ex.Message}");
            }
            return null;
        }

        static void UpdateIpInDatabase(string newIp)
        {
            try
            {
                // SQL-dən gələ biləcək problemlərin (sqlinjection və ya dırnaq xətaları) qarşısını almaq üçün təhlükəsizləşdiririk
                string safeIp = newIp.Replace("'", "''");
                string connString = GetConnectionString();
                
                Console.WriteLine($"[INFO] SQL Serverə qoşulur: {connString}");

                using (var conn = new SqlConnection(connString))
                {
                    conn.Open();
                    // SQL Server 2005-də parametrlərlə bağlı mümkün problemləri (NVARCHAR çevirmələri) istisna etmək üçün inline SQL istifadə edirik
                    string updateSql = $"UPDATE TankConfig SET ip = '{safeIp}'";
                    using (var cmd = new SqlCommand(updateSql, conn))
                    {
                        int rows = cmd.ExecuteNonQuery();
                        if (rows == 0)
                        {
                            // Əgər cədvəl tamamilə boşdursa, UPDATE 0 sətirə təsir edir.
                            string insertSql = $"INSERT INTO TankConfig (ip) VALUES ('{safeIp}')";
                            using (var insertCmd = new SqlCommand(insertSql, conn))
                            {
                                int inserted = insertCmd.ExecuteNonQuery();
                                Console.WriteLine($"[\u2714] Cədvəl boş idi, {inserted} yeni sətir əlavə olundu və IP yazıldı: {newIp}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[\u2714] IP ünvan bazada olan bütün {rows} sətrə '{newIp}' olaraq uğurla yazıldı.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n=========================================");
                Console.WriteLine($"[\u26A0] DİQQƏT! VERİLƏNLƏR BAZASINA YAZILARKƏN XƏTA BAŞ VERDİ!");
                Console.WriteLine($"Xəta mesajı: {ex.Message}");
                Console.WriteLine($"Səbəb ola bilər: Cədvəl yoxdur, icazə yoxdur, və ya Server Adı səhvdir.");
                Console.WriteLine($"=========================================\n");
                Console.ResetColor();
                Console.WriteLine("Zəhmət olmasa xətanı oxuyun və davam etmək üçün ENTER basın...");
                Console.ReadLine();
            }
        }

        static async Task Main(string[] args)
        {
            // Konsolda Azərbaycan dilini (Ü,Ö,Ğ,Ç,Ş,I,Ə) tam dəstəkləmək üçün
            Console.OutputEncoding = Encoding.UTF8;

            int devicePort = 5656;
            int tankCount = 1; // Default çən sayı
            string deviceIp = null;

            // C:\Abak\STPARAM.ini faylından oxumaq
            string iniFilePath = @"C:\Abak\STPARAM.ini";
            try
            {
                if (System.IO.File.Exists(iniFilePath))
                {
                    string[] lines = System.IO.File.ReadAllLines(iniFilePath);
                    foreach (string line in lines)
                    {
                        if (line.Trim().StartsWith("TANK_COUNT"))
                        {
                            var parts = line.Split('=');
                            if (parts.Length > 1)
                            {
                                int.TryParse(parts[1].Trim(), out tankCount);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[\u26A0] Diqqət: INI faylı oxunarkən xəta baş verdi: {ex.Message}");
            }

            Console.WriteLine($"--- Windbell WB-SS200 Test Başladı ---");
            Console.WriteLine($"--- Oxunacaq çən sayı: {tankCount} ---");

            while (true)
            {
                int maxRetries = 3;
                bool connectionSuccess = false;

                if (string.IsNullOrEmpty(deviceIp))
                {
                    deviceIp = GetIpFromDatabase();
                }

                if (string.IsNullOrEmpty(deviceIp))
                {
                    Console.Write($"\nBazada IP ünvanı tapılmadı.\nZəhmət olmasa IP ünvanı daxil edin: ");
                    deviceIp = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(deviceIp))
                    {
                        UpdateIpInDatabase(deviceIp);
                    }
                }

                Console.WriteLine($"\n[{deviceIp}:{devicePort}] cihazına qoşulmağa cəhd edilir...");

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        using (TcpClient client = new TcpClient())
                        {
                            // Qoşulmağa cəhd (3 saniyə timeout)
                            var connectTask = client.ConnectAsync(deviceIp, devicePort);
                            if (await Task.WhenAny(connectTask, Task.Delay(3000)) != connectTask)
                            {
                                throw new Exception("Bağlantı vaxtı bitdi (Timeout).");
                            }

                            using (NetworkStream stream = client.GetStream())
                            {
                                // 1. Sorğu komandası 
                                var tankList = new List<string>();
                                for (int i = 1; i <= tankCount; i++)
                                {
                                    tankList.Add($"\"Tank{i}\"");
                                }
                                string request = $"{{\"tanks\": [{string.Join(", ", tankList)}], \"requestType\": \"status\"}}";
                                byte[] requestBytes = Encoding.UTF8.GetBytes(request);
                                await stream.WriteAsync(requestBytes, 0, requestBytes.Length);

                                // 2. Cavabı tam göndərilənədək oxumaq
                                StringBuilder responseBuilder = new StringBuilder();
                                byte[] buffer = new byte[8192];
                                AtgResponse result = null;
                                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                                while (true)
                                {
                                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                                    if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask) 
                                    {
                                        throw new Exception("Cihazdan sonrakı məlumatın gəlməsi gecikdi (Timeout).");
                                    }

                                    int bytesRead = await readTask;
                                    if (bytesRead == 0)
                                    {
                                        throw new Exception("Bağlantı qarşı tərəfdən vaxtından əvvəl kəsildi.");
                                    }

                                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                                    responseBuilder.Append(chunk);
                                    string currentResponse = responseBuilder.ToString();

                                    try
                                    {
                                        result = JsonSerializer.Deserialize<AtgResponse>(currentResponse, options);
                                        if (result != null)
                                        {
                                            break; // Tam ölçülü JSON
                                        }
                                    }
                                    catch (JsonException)
                                    {
                                        // JSON hələ bitməyib, oxumağa davam et
                                    }
                                }

                                // 3. JSON-u ekrana çıxarmaq
                                if (result != null && result.data != null)
                                {
                                    Console.WriteLine($"\n===================== CİHAZ (ATG) MƏLUMATLARI =====================");
                                    if (result.metadata != null)
                                    {
                                        Console.WriteLine($"   Datanın vaxtı: {result.metadata.timestamp} | Sorğu ID: {result.metadata.request_id}");
                                    }
                                    Console.WriteLine(new string('=', 67));

                                    var receivedTanks = result.data.Select(t => t.tank_id).ToList();
                                    var missingTanks = new List<int>();
                                    for (int i = 1; i <= tankCount; i++)
                                    {
                                        if (!receivedTanks.Contains(i))
                                            missingTanks.Add(i);
                                    }

                                    if (missingTanks.Count > 0)
                                    {
                                        Console.ForegroundColor = ConsoleColor.Yellow;
                                        Console.WriteLine($" [\u26A0] XƏBƏRDARLIQ: Cihazdan aşağıdakı çənlərin məlumatı heç gəlmədi: {string.Join(", ", missingTanks)}");
                                        Console.ResetColor();
                                        Console.WriteLine(new string('-', 67));
                                    }

                                    foreach (var tank in result.data.OrderBy(t => t.tank_id))
                                    {
                                        if (tank.error != null)
                                        {
                                            Console.ForegroundColor = ConsoleColor.Red;
                                            Console.WriteLine($" [ÇƏN {tank.tank_id}] XƏTA");
                                            Console.WriteLine($" Səbəb: {tank.error.message} (Kod: {tank.error.code})");
                                            Console.ResetColor();
                                        }
                                        else
                                        {
                                            bool isMissingParams = tank.oil_level == null || tank.volume == null || tank.temperature == null;

                                            Console.ForegroundColor = ConsoleColor.Cyan;
                                            Console.WriteLine($" [ÇƏN {tank.tank_id}] MƏHSUL: {tank.product_code ?? "Bilinmir"} | STATUS: {tank.sensor_status?.ToUpper() ?? "BİLİNMİR"}");
                                            Console.ResetColor();

                                            if (isMissingParams)
                                            {
                                                Console.ForegroundColor = ConsoleColor.Yellow;
                                                Console.WriteLine($"  [\u26A0] Diqqət: Çəndən gələn məlumatda bəzi fiziki dəyərlər (həcm, temperatur və s.) yarımçıqdır!");
                                                Console.ResetColor();
                                            }

                                            Console.WriteLine($"  ► Səviyyə:   Yanacaq: {tank.oil_level?.ToString() ?? "?"} mm | Su: {tank.water_level?.ToString() ?? "?"} mm | Boşluq (Ullage): {tank.Ullage?.ToString() ?? "?"} mm");
                                            Console.WriteLine($"  ► Həcm:      Təmiz həcm (Tc): {tank.tc_volume?.ToString() ?? "?"} L | Ümumi həcm: {tank.volume?.ToString() ?? "?"} L | Su həcmi: {tank.water_volume?.ToString() ?? "?"} L");
                                            Console.WriteLine($"  ► Əlavə:     Tutum (Capacity): {tank.capacity?.ToString() ?? "?"} L | Temperatur: {tank.temperature?.ToString() ?? "?"} °C");
                                        }
                                        Console.WriteLine(new string('-', 67));
                                    }
                                }
                            }
                        }

                        connectionSuccess = true;
                        break; 
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\nXəta (Cəhd {attempt}/{maxRetries}): {ex.Message}");
                        Console.ResetColor();

                        if (attempt < maxRetries)
                        {
                            Console.WriteLine("2 saniyə sonra yenidən cəhd edilir...");
                            await Task.Delay(2000); 
                        }
                    }
                } // for loop sonu

                if (!connectionSuccess)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.WriteLine($"\n[MƏLUMAT KƏSİNTİSİ] Bütün {maxRetries} cəhdin hamısı uğursuz oldu.");
                    Console.ResetColor();

                    Console.Write("Zəhmət olmasa yeni IP ünvanı daxil edin: ");
                    string newIp = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(newIp))
                    {
                        deviceIp = newIp;
                        UpdateIpInDatabase(deviceIp);
                    }
                    else
                    {
                        Console.WriteLine("\n30 saniyə gözlənilir...");
                        await Task.Delay(30000);
                    }
                }
                else
                {
                    Console.WriteLine("\n30 saniyə gözlənilir... (Dayandırmaq üçün Ctrl+C)");
                    await Task.Delay(30000);
                }
            }
        }
    }
}