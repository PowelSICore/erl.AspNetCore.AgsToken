using System;
using System.Threading.Tasks;
using erl.AspNetCore.AgsToken;
using Microsoft.Extensions.Configuration;

namespace AgsToken.ConsoleTester
{
    class Program
    {
        static async Task Main(string[] args)
        {

            var builder = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();

            var _options = new AgsOptions();
            builder.GetSection("ArcGisServer").Bind(_options);

     
            try
            {
               var tokenData = await AgsServer.GenerateToken(_options.Scheme, _options.Host, _options.Port, _options.Instance, _options.Username, _options.Password);
               Console.WriteLine($"token:  {tokenData.token}");
               Console.ReadKey();
            }
            catch
            {
                //failed to get token the usual way. 
            }

            

        }
    }
}
