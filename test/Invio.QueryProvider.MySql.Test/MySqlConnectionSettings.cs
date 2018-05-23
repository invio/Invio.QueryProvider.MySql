using System;
using MySql.Data.MySqlClient;

namespace Invio.QueryProvider.MySql.Test {
    public class MySqlConnectionSettings {

        public String User { get; set; } = "root";
        public String Password { get; set; }
        public UInt16 Port { get; set; } = 3306;
        public String Host { get; set; } = "127.0.0.1";
        public MySqlSslMode SslMode { get; set; } = MySqlSslMode.None;

    }
}
