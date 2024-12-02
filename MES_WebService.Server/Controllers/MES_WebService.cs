using Microsoft.AspNetCore.Mvc;
using MES_WebService.Server.Data;
using MES_WebService.Server.Models;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Data.Common;


namespace MES_WebService.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MES_WebService : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly IConfiguration _configuration;

        public MES_WebService(ApplicationDbContext dbContext, IConfiguration configuration)
        {
            _dbContext = dbContext;
            _configuration = configuration;
        }

        [HttpGet("GetRunningNumbers")]
        public async Task<IActionResult> GetRunningNumbers(string SequenceName, int RunNoCount, string LotId, int StepNo, string Factory)
        {
            try
            {
                // Check if the lot id exist in the Logs table.
                var existinglot = await _dbContext.Logs.FirstOrDefaultAsync(lot => lot.LotId == LotId && lot.SequenceName == SequenceName);
                var runningNumbers = new List<long>();
                var newRunningNumbers = new List<long>();

                var generatedRunningNumbers = new List<string>();

                var existingLogs = new List<Log>();
                var existingActiveLogs = new List<Log>();
                var existingInactiveLogs = new List<Log>();



                // If the lot does not exist then directly proceed to get the requested number of running number.
                if (existinglot == null)
                {
                    for (int i = 0; i < RunNoCount; i++)
                    {
                        var nextValue = await GetNextSequenceValueAsync(SequenceName);
                        newRunningNumbers.Add(nextValue);

                        var generatedNumber = await GenerateRunningNumber(Factory, LotId, nextValue);
                        generatedRunningNumbers.Add(generatedNumber);
                    }
                }
                
                else
                {
                    // Get all existing logs for the received LotId and SequenceName
                    existingLogs = await GetExistingActiveLogs(LotId, SequenceName);

                    // If the existing active logs are greater than or equal to the requested running
                    // numbers, then get all the required running numbers from the existing logs
                    if (existingLogs.Count >= RunNoCount)
                    {
                        existingActiveLogs.AddRange(existingLogs.Take(RunNoCount));

                        existingInactiveLogs.AddRange(existingLogs.Skip(RunNoCount));

                        foreach( var log in existingActiveLogs)
                        {
                            log.DeleteFlag = false;
                        }

                        if (existingActiveLogs.Count > 0)
                        {
                            // Update the existing logs with delete flag set to false
                            _dbContext.Logs.UpdateRange(existingActiveLogs);
                        }


                        foreach (var log in existingInactiveLogs)
                        {
                            log.DeleteFlag = true;
                        }

                        if (existingInactiveLogs.Count > 0)
                        {
                            // Update the excess existing active logs with delete flag set to true
                            _dbContext.Logs.UpdateRange(existingInactiveLogs);
                        }
                    }
                    else
                    {
                        // Add all existing active logs to the result
                        if(existingLogs.Count > 0)
                        {
                            existingActiveLogs.AddRange(existingLogs);

                            foreach (var log in existingActiveLogs)
                            {
                                log.DeleteFlag = false;
                            }

                            if (existingActiveLogs.Count > 0)
                            {
                                // Update the existing logs with delete flag set to false
                                _dbContext.Logs.UpdateRange(existingActiveLogs);
                            }
                        }

                        // If the existing logs are less than the requested running numbers,
                        // then get the remaining running numbers from the sequence.
                        var requiredRunNumbers = RunNoCount - existingActiveLogs.Count;

                        for (int i = 0; i < requiredRunNumbers; i++)
                        {
                            // Get the remaining running numbers from the sequence
                            var nextValue = await GetNextSequenceValueAsync(SequenceName);
                            newRunningNumbers.Add(nextValue);


                            var generatedNumber = await GenerateRunningNumber(Factory, LotId, nextValue);
                            generatedRunningNumbers.Add(generatedNumber);
                        }
                    }

                }

                // Check for duplicates between generated running numbers and existing logs
                var duplicateGeneratedRunningNumbers = generatedRunningNumbers
                    .Where(grn => existingLogs.Any(log => log.GeneratedRunningNumber == grn))
                    .ToList();

                // If duplicates exist, return an error
                if (duplicateGeneratedRunningNumbers.Count > 0)
                {
                    return BadRequest(new
                    {
                        message = "Duplicate running numbers found.",
                        duplicates = duplicateGeneratedRunningNumbers
                    });
                }


                var newLogs = new List<Log>();

                if (newRunningNumbers.Count > 0)
                {

                    // Update the Log table with new running numbers
                    newLogs = newRunningNumbers.Select((rn, index) => new Log
                    {
                        SequenceName = SequenceName,
                        RunningNumber = rn,
                        RunningNumberIndex = 0,
                        GeneratedRunningNumber = "",
                        LotId = LotId,
                        DeleteFlag = false,
                        TimeStamp = DateTimeOffset.UtcNow,
                    }).ToList();

                    for ( int i=0; i<newLogs.Count; i++)
                    {
                        newLogs[i].GeneratedRunningNumber = generatedRunningNumbers[i];
                    }

                    for (int i = 0; i < newLogs.Count; i++)
                    {
                        newLogs[i].RunningNumberIndex = existingActiveLogs.Count + i + 1;
                    }

                    // Add the new logs to the database
                    _dbContext.Logs.AddRange(newLogs);
                }

                await _dbContext.SaveChangesAsync();

                var totalRunningNumLogs = new List<Log>();
                totalRunningNumLogs.AddRange(existingActiveLogs);
                totalRunningNumLogs.AddRange(newLogs);

                //// Return the requested running numbers with detailed logs
                var totalGeneratedRunningNumbers = totalRunningNumLogs.Select(log => log.GeneratedRunningNumber).ToList();
                return Ok(totalGeneratedRunningNumbers);


            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message});
            }
        }


        private async Task<List<Log>> GetExistingActiveLogs(string LotId, string SequenceName) 
        { 
            return await _dbContext.Logs
                        .Where(log => log.LotId == LotId && log.SequenceName == SequenceName)
                        .ToListAsync();
        }


        private async Task<long> GetNextSequenceValueAsync(string SequenceName)
        {
            var connection = _dbContext.Database.GetDbConnection();
            await connection.OpenAsync();

            try
            {
                using (var command = connection.CreateCommand())
                {
                    var sequenceExists = await CheckSequenceExistsAsync(SequenceName, connection);

                    if (!sequenceExists)
                    {
                        await CreateSequenceAsync(SequenceName, connection);
                    }

                    command.CommandText = $"SELECT NEXT VALUE FOR dbo.{SequenceName};";

                    var result = await command.ExecuteScalarAsync();

                    return Convert.ToInt64(result);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(ex.Message);
            }
            finally
            {
                await connection.CloseAsync();
            }
        }

        private async Task<bool> CheckSequenceExistsAsync(string SequenceName, DbConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT COUNT(*) FROM sys.sequences WHERE name = '{SequenceName}'";
                var count = (int)await command.ExecuteScalarAsync();
                return count > 0;
            }
        }

        private async Task CreateSequenceAsync(string SequenceName, DbConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"CREATE SEQUENCE {SequenceName} START WITH 1 INCREMENT BY 1;";
                await command.ExecuteNonQueryAsync();
            }
        }

        private async Task<string> GenerateRunningNumber(string factory, string lotId, long runnNo)
        {
            var connectionString = _configuration.GetConnectionString("OracleConnection");

            OracleConnection conn = new OracleConnection(connectionString);
            try
            {
                await conn.OpenAsync();

                var commandText = "SELECT WEBSVC_WBD_RUNNO(:p_FACTORY, :p_LOT_ID, :p_RUNNO) FROM dual";
                //var commandText = "SELECT WEBSVC_WBD_RUNNO('UAT', '2423000870008', '00001') FROM dual";

                using var command = new OracleCommand(commandText, conn);

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
            finally
            {
                await conn.CloseAsync();
            }
        }
    }
}
