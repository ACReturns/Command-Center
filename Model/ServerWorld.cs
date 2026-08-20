using System.Collections.Generic;

namespace CommandCenter.Model
{
    // Matches the shape of the existing Servers\*_server_status.json files.
    // Unrecognized fields (e.g. ServerTypeName) are ignored automatically by System.Text.Json.
    public class ServerEndpoint
    {
        public int No { get; set; }
        public string IP { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    public class ServerWorld
    {
        public string Name { get; set; } = string.Empty;
        public int No { get; set; }
        public List<ServerEndpoint> GameServer { get; set; } = new();
        public ServerEndpoint? ShopServer { get; set; }
    }
}
