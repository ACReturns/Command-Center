using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Model;

namespace CommandCenter.Services
{
    public static class PortStatusService
    {
        public static async Task<bool> IsWorldOnlineAsync(ServerWorld world, TimeSpan timeout)
        {
            var endpoint = world.GameServer.Count > 0 ? world.GameServer[0] : world.ShopServer;
            if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.IP))
            {
                return false;
            }

            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);

            try
            {
                await client.ConnectAsync(endpoint.IP, endpoint.Port, cts.Token);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
