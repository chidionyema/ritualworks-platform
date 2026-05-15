using Npgsql;
using System;
using System.Threading.Tasks;
using System.Threading;

class Program {
    static async Task Main() {
        var builder = new NpgsqlDataSourceBuilder("Host=localhost;Username=old");
        builder.UsePeriodicPasswordProvider((sb, ct) => {
            sb.Username = "new_user";
            return new ValueTask<string>("new_pwd");
        }, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(0));
        var ds = builder.Build();
        var conn = ds.CreateConnection();
        Console.WriteLine($"Conn string: {conn.ConnectionString}");
        await Task.CompletedTask;
    }
}
