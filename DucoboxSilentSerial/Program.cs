
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DucoboxSilentSerial
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureServices(services =>
                {
                    services.AddHostedService<Worker>();
                })
                .Build();

            await host.RunAsync();
        }
    }
}