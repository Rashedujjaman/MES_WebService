using Microsoft.AspNetCore.Mvc;
using MES_WebService.Server.Data;
using MES_WebService.Server.Models;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
using System.Configuration;
//using System.Configuration;

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
        public async Task<IActionResult> GetRunningNumbers(string SequenceName, int RunNoCount, string LotId, int StepNo)
        {
            try
            {
                // Check if the lot id exist in the Logs table.
                var existinglot = await _dbContext.Logs.FirstOrDefaultAsync(lot => lot.LotId == LotId && lot.SequenceName == SequenceName);

                // Declare a variable to store all the running numbers requested.
                var runningNumbers = new List<long>();
                
                // Declare a variable to store only newly generated the running numbers for the request requested.
                var newRunningNumbers = new List<long>();

                var generatedRunningNumber = new List<string>();

                // Declare a variable to store the logs that are being used from existing logs.
                var existingLogs = new List<Log>();

                // Declare a variable to store the existing logs that needs to be activated.
                var existingActiveLogs = new List<Log>();

                // Declare a variable to store the existing logs that needs to be inactivated.
                var existingInactiveLogs = new List<Log>();



                // If the lot does not exist then directly proceed to get the requested number of running number.
                if (existinglot == null)
                {
                    for (int i = 0; i < RunNoCount; i++)
                    {
                        var nextValue = await GetNextSequenceValueAsync(SequenceName);
                        newRunningNumbers.Add(nextValue);

                        var generatedNumber = await GenerateRunningNumber("USP", LotId, nextValue.ToString());
                        generatedRunningNumber.Add(generatedNumber);
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


                            var generatedNumber = await GenerateRunningNumber("USP", LotId, nextValue.ToString());
                            generatedRunningNumber.Add(generatedNumber);
                        }
                    }

                }

                if (newRunningNumbers.Count > 0)
                {
                    // Update the Log table with new running numbers
                    var newLogs = newRunningNumbers.Select((rn, index) => new Log
                    {
                        SequenceName = SequenceName,
                        RunningNumber = rn,
                        RunningNumberIndex = index,
                        ConvertedRunningNumber = "UN24"+rn.ToString().PadLeft(8, '0'),
                        LotId = LotId,
                        DeleteFlag = false,
                        TimeStamp = DateTimeOffset.UtcNow,
                    }).ToList();

                    // Add the new logs to the database
                    _dbContext.Logs.AddRange(newLogs);
                }

                // Save changes for both updates and new inserts
                await _dbContext.SaveChangesAsync();


                runningNumbers.AddRange(existingActiveLogs.Select(log => log.RunningNumber));
                runningNumbers.AddRange(newRunningNumbers);
                // Return the requested running numbers
                return Ok(runningNumbers);

                //var totalRunningNumLogs = new List<Log>();
                //totalRunningNumLogs.AddRange(existingUsedLogs);
                //totalRunningNumLogs.AddRange(newLogs);
                //// Return the requested running numbers with detailed logs
                //return Ok(totalRunningNumLogs);


            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message});
            }
        }

        // Method to get existing active logs
        private async Task<List<Log>> GetExistingActiveLogs(string LotId, string SequenceName) 
        { 
            return await _dbContext.Logs
                        .Where(log => log.LotId == LotId && log.SequenceName == SequenceName)
                        .ToListAsync();
        }


        // Method to get the next value from the sequence
        private async Task<long> GetNextSequenceValueAsync(string SequenceName)
        {
            // Get the database connection
            var connection = _dbContext.Database.GetDbConnection();

            // Open the connection
            await connection.OpenAsync();

            using (var command = connection.CreateCommand())
            {
                try
                {
                    // Create a SQL query to get next value of the sequence
                    command.CommandText = $"SELECT NEXT VALUE FOR dbo.{SequenceName};";

                    // Execute the query
                    var result = await command.ExecuteScalarAsync();
                    await connection.CloseAsync();

                    // Return the value
                    return Convert.ToInt64(result);
                }
                catch (Exception ex)
                {
                    // Log the exception or handle it as needed
                    throw new InvalidOperationException(ex.Message);
                }
            }
        }

        public async Task<string> GenerateRunningNumber(string factory, string lotId, string runNo)
        {
            // Get the Oracle connection string from the configuration
            var connectionString = _configuration.GetConnectionString("OracleConnection");

            using (var connection = new OracleConnection(connectionString))
            {
                try
                {
                    // Open the Oracle connection
                    await connection.OpenAsync();

                    // Define the Oracle command to call the database function
                    var commandText = "SELECT WEBSVC_WBD_RUNNO(:factory, :lotId, :runNo) FROM dual";

                    using var command = new OracleCommand(commandText, connection);
                    
                    // Add parameters for the Oracle function
                    command.Parameters.Add("factory", OracleDbType.Varchar2).Value = factory;
                    command.Parameters.Add("lotId", OracleDbType.Varchar2).Value = lotId;
                    command.Parameters.Add("runNo", OracleDbType.Varchar2).Value = runNo;

                    // Execute the command and fetch the result
                    var result = await command.ExecuteScalarAsync();
                    return result?.ToString();
                    
                }
                catch (Exception ex)
                {
                    // Handle errors and throw an exception with context
                    throw new ApplicationException($"Error executing Oracle command: {ex.Message}", ex);
                }
                finally
                {
                    // Ensure the connection is closed
                    await connection.CloseAsync();
                }
            }
        }
    }
}
