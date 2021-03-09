using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace erl.AspNetCore.AgsToken
{
    public static class AgsServer
    {
        public static async Task<AgsTokenResponse> GenerateToken(string scheme, string server, string port, string instance, string username, string password)
        {
            var tokenUri = $"{scheme}://{server}:{port}/{instance}/admin/generateToken";

            
            using (var wc = new HttpClient())
            {
                wc.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", username),
                    new KeyValuePair<string, string>("password", password),
                    new KeyValuePair<string, string>("client", "requestip"),
                    new KeyValuePair<string, string>("f", "pjson")
                });


                try
                {
                    var response = await wc.PostAsync(tokenUri, content);

                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync();
                    var token = DeserializeJson<AgsTokenResponse>(json);
                    if (token != null && token.IsValid())
                        return token;
                }
                catch
                {
                }


                try
                {
                    //Try the Network Collector way(copied code from Dinesh)
                    var baseUrl = $"{scheme}://{server}:{port}/{instance}";
                        var op = new Operation(baseUrl);
                        var referer = baseUrl;
                        var tokenData = await op.Authenticate(username, password, null, referer);
                        return tokenData;

                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }

        private static T DeserializeJson<T>(string serialized) where T : class
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var memoryStream = new MemoryStream())
            using (var writer = new StreamWriter(memoryStream))
            {
                writer.Write(serialized);
                writer.Flush();

                memoryStream.Position = 0;

                return serializer.ReadObject(memoryStream) as T;
            }
        }
    }
}