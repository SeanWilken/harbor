/// Applies db/up/*.sql with grate at hub startup. grate records a hash per one-time script
/// and fails the run if an applied script has since been edited.
module Harbor.Migrations

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open grate.Configuration
open grate.DependencyInjection
open grate.Migration
open grate.postgresql.DependencyInjection

/// Scripts ship next to the binary (see Harbor.fsproj), so this works for `dotnet run` and publish.
let defaultDir = Path.Combine(AppContext.BaseDirectory, "db")

let run (connectionString: string) (sqlDir: string) : Task =
    task {
        let config =
            GrateConfigurationBuilder
                .Create()
                .WithConnectionString(connectionString)
                .WithSqlFilesDirectory(sqlDir)
                .WithOutputFolder(Path.Combine(Path.GetTempPath(), "harbor-grate"))
                .DoNotCreateDatabase()
                .WithTransaction(true)
                .Build()
        use services =
            ServiceCollection()
                .AddLogging(fun b -> b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning) |> ignore)
                .AddGrate(config, fun _ -> ())
                .UsePostgreSQL()
                .BuildServiceProvider()
        do! services.GetRequiredService<IGrateMigrator>().Migrate()
    }
