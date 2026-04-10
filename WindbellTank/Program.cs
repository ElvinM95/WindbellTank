using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

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
        static async Task Main(string[] args)
        {
            // Konsolda Azərbaycan dilini (Ü,Ö,Ğ,Ç,Ş,I,Ə) tam dəstəkləmək üçün
            Console.OutputEncoding = Encoding.UTF8;

            // !!! BU HİSSƏNİ CİHAZIN AYARLARINA GÖRƏ DƏYİŞ !!!
            string deviceIp = "10.40.7.35"; // Default dəyər
            int devicePort = 5656;
            int tankCount = 1; // Default çən sayı

            // C:\Abak\STPARAM.ini faylından məlumatları oxumaq
            string iniFilePath = @"C:\Abak\STPARAM.ini";
            try
            {
                if (System.IO.File.Exists(iniFilePath))
                {
                    string[] lines = System.IO.File.ReadAllLines(iniFilePath);
                    foreach (string line in lines)
                    {
                        if (line.Trim().StartsWith("TANK_IP"))
                        {
                            var parts = line.Split('=');
                            if (parts.Length > 1)
                            {
                                deviceIp = parts[1].Trim();
                            }
                        }
                        else if (line.Trim().StartsWith("TANK_COUNT"))
                        {
                            var parts = line.Split('=');
                            if (parts.Length > 1)
                            {
                                int.TryParse(parts[1].Trim(), out tankCount);
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"[\u26A0] Diqqət: {iniFilePath} tapılmadı. Default dəyərlər istifadə olunur.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[\u26A0] Diqqət: INI faylı oxunarkən xəta baş verdi: {ex.Message}");
            }

            Console.WriteLine($"--- Windbell WB-SS200 Test Başladı ({deviceIp}) ---");
            Console.WriteLine($"--- Oxunacaq çən sayı: {tankCount} ---");

            while (true)
            {
                int maxRetries = 3;

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
                            // 1. Sorğu komandası (Dinamik çən sayına əsasən formalaşır)
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
                                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask) // Hissə oxuma üçün maksimum 5 saniyə gözləmə
                                {
                                    throw new Exception("Cihazdan sonrakı məlumatın gəlməsi gecikdi (Timeout). Məlumat tam alınmadı.");
                                }

                                int bytesRead = await readTask;
                                if (bytesRead == 0)
                                {
                                    throw new Exception("Bağlantı qarşı tərəfdən vaxtından əvvəl kəsildi. (Məlumat natamamdır)");
                                }

                                string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                                responseBuilder.Append(chunk);
                                string currentResponse = responseBuilder.ToString();

                                try
                                {
                                    result = JsonSerializer.Deserialize<AtgResponse>(currentResponse, options);
                                    if (result != null)
                                    {
                                        break; // Tam ölçülü və etibarlı JSON əldə edildi
                                    }
                                }
                                catch (JsonException)
                                {
                                    // JSON hələ tam formalaşmayıb (bitməyib), oxumağa davam et
                                }
                            }

                            // 3. JSON-u ekrana səliqəli çıxarmaq
                            if (result != null && result.data != null)
                            {
                                Console.WriteLine($"\n===================== CİHAZ (ATG) MƏLUMATLARI =====================");
                                if (result.metadata != null)
                                {
                                    Console.WriteLine($"   Datanın vaxtı: {result.metadata.timestamp} | Sorğu ID: {result.metadata.request_id}");
                                }
                                Console.WriteLine(new string('=', 67));

                                // Bütün sorğulanan çənlərin tapılıb-tapılmadığını yoxlamaq
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

                    // Əgər kod xətasız bura çatıbsa, deməli tam məlumat uğurla gəlib. 
                    // 'for' təkrar cəhd dövrəsindən (retry loop) çıxırıq.
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
                        await Task.Delay(2000); // Təkrar qoşulmazdan əvvəl kiçik fasilə (2 saniyə)
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        Console.WriteLine($"\n[MƏLUMAT KƏSİNTİSİ] Bütün {maxRetries} cəhdin hamısı uğursuz oldu. 30 saniyəlik növbəti period gözlənilir.");
                        Console.ResetColor();
                    }
                }
            } // for loop sonu

            Console.WriteLine("\n30 saniyə gözlənilir... (Dayandırmaq üçün Ctrl+C)");
                await Task.Delay(30000);
            }
        }
    }
}