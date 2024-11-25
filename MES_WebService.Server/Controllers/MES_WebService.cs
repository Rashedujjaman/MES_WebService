using Microsoft.AspNetCore.Mvc;
using MES_WebService.Server.Data;
using MES_WebService.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace MES_WebService.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MES_WebService : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;

        public MES_WebService(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
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

                // Declare a variable to store the existing active logs for the received LotId.
                var existingActiveLogs = new List<Log>();

                // Declare a variable to store the logs that are being used from existing logs.
                var existingUsedLogs = new List<Log>();

                // If the lot does not exist then directly proceed to get the requested number of running number.
                if (existinglot == null)
                {
                    for (int i = 0; i < RunNoCount; i++)
                    {
                        var nextValue = await GetNextSequenceValueAsync(SequenceName);
                        newRunningNumbers.Add(nextValue);
                    }
                }
                
                else
                {
                    // Get the existing logs for the received LotId from the Logs table
                    // where delete flag is false.
                    existingActiveLogs = await GetExistingActiveLogs(LotId, SequenceName);

                    // If the existing active logs are greater than or equal to
                    // the requested running numbers, then get the running numbers from the existing logs
                    if (existingActiveLogs.Count() >= RunNoCount)
                    {

                        //runningNumbers.AddRange(existingActiveLogs.Take(RunNoCount).Select(log => log.RunningNumber));

                        existingUsedLogs.AddRange(existingActiveLogs.Take(RunNoCount));

                        var existingInactiveLogs = existingActiveLogs.Skip(RunNoCount);

                        foreach (var log in existingInactiveLogs)
                        {
                            log.DeleteFlag = true;
                        }

                        // Update the excess existing active logs with delete flag set to true
                        _dbContext.Logs.UpdateRange(existingInactiveLogs);
                    }
                    else
                    {
                        // Add all existing active logs to the result
                        if(existingActiveLogs.Count() > 0)
                        {
                            existingUsedLogs.AddRange(existingActiveLogs);
                        }

                        // If the existing active logs are less than the requested running numbers,
                        // then get the remaining running numbers from the sequence.
                        var requiredRunNumbers = RunNoCount - existingUsedLogs.Count;

                        for (int i = 0; i < requiredRunNumbers; i++)
                        {
                            // Get the remaining running numbers from the sequence
                            var nextValue = await GetNextSequenceValueAsync(SequenceName);
                            newRunningNumbers.Add(nextValue);
                        }
                    }

                }

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

                // Save changes for both updates and new inserts
                await _dbContext.SaveChangesAsync();


                runningNumbers.AddRange(existingUsedLogs.Select(log => log.RunningNumber));
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
                        .Where(log => log.LotId == LotId && log.SequenceName == SequenceName && log.DeleteFlag == false)
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
    }
}
