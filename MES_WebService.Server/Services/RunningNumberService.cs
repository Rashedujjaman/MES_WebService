using Oracle.ManagedDataAccess.Client;
using System;
using System.Threading.Tasks;

public interface IRunningNumberService
{
    Task<string> GenerateRunningNumberAsync(string factory, string lotId, long runnNo);
}

public class RunningNumberService : IRunningNumberService
{
    private readonly IConfiguration _connectionString;

    public RunningNumberService(IConfiguration configuration)
    {
        _connectionString = configuration;
    }

    public async Task<string> GenerateRunningNumberAsync(string factory, string lotId, long runnNo)
    {
        var connectionString = _connectionString.GetConnectionString("OracleConnection");

        await using var conn = new OracleConnection(connectionString);

        try
        {
            await conn.OpenAsync();

            var commandText = "SELECT WEBSVC_WBD_RUNNO(:p_FACTORY, :p_LOT_ID, :p_RUNNO) FROM dual";
            //var commandText = "SELECT WEBSVC_WBD_RUNNO('UAT', '2423000870008', '00001') FROM dual"; //example value.

            await using var command = new OracleCommand(commandText, conn);

            command.Parameters.Add("p_FACTORY", OracleDbType.Varchar2).Value = factory;
            command.Parameters.Add("p_LOT_ID", OracleDbType.Varchar2).Value = lotId;
            command.Parameters.Add("p_RUNNO", OracleDbType.Varchar2).Value = runnNo.ToString().PadLeft(5, '0');

            var result = await command.ExecuteScalarAsync();

            return result.ToString();
        }
        catch (Exception ex)
        {
            throw new ApplicationException($"Error executing Oracle command: {ex.Message}", ex);
        }
    }
}

