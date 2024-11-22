using Microsoft.AspNetCore.Mvc;
using MES_WebService.Server.Data;
using MES_WebService.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace MES_WebService.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RMS_WebService : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public RMS_WebService(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet("GetRunningNumbers")]
        public async Task<IActionResult> GetRunningNumbers(string SequenceName, int RunNoCount, string LotId, int StepNo)
        {
            try
            {
                // Check if the lot id exist in the Logs table.
                var existinglot = await _context.Logs.FirstOrDefaultAsync(l => l.LotId == LotId);

                // Declare a variable to store the running numbers requested.
                var runningNumbers = new List<long>();

                // Declare a variable to store the existing active logs for the received LotId.
                var existingActiveLogs = new List<Log>();

                // If the lot does not exist then directly proceed to get the requested number of running number.
                if (existinglot == null)
                {
                    for (int i = 0; i < RunNoCount; i++)
                    {
                        var nextValue = await GetNextSequenceValueAsync(SequenceName);
                        runningNumbers.Add(nextValue);
                    }
                }
                
                else
                {
                    // Get the existing running numbers for the received LotId from the Logs table
                    // where delete flag is false.
                    existingActiveLogs = await _context.Logs
                        .Where(log => log.LotId == LotId && log.DeleteFlag == false)
                        .ToListAsync();

                    if (existingActiveLogs.Count() >= RunNoCount)
                    {
                        // If the existing active logs are greater than or equal to
                        // the requested running numbers, then get the running numbers from the existing logs
                        runningNumbers.AddRange(existingActiveLogs.Take(RunNoCount).Select(log => log.RunningNumber));

                        foreach (var log in existingActiveLogs.Take(RunNoCount))
                        {
                            log.DeleteFlag = true;
                        }
                    }
                    else
                    {
                        // If the existing active logs are less than the requested running numbers,
                        // then get the remaining running numbers from the sequence.
                        var requiredRunNumbers = RunNoCount - existingActiveLogs.Count;

                        // Add all existing active logs to the result
                        runningNumbers.AddRange(existingActiveLogs.Select(log => log.RunningNumber));
                        foreach(var log in existingActiveLogs)
                        {
                            log.DeleteFlag = true;
                        }

                        for (int i = 0; i < requiredRunNumbers; i++)
                        {
                            // Get the remaining running numbers from the sequence
                            var nextValue = await GetNextSequenceValueAsync(SequenceName);
                            runningNumbers.Add(nextValue);
                        }
                    }

                    // Update the existing logs with delete flag set to true
                    _context.Logs.UpdateRange(existingActiveLogs);
                }
                // Skip the existing running numbers and get the new running numbers to construct new logs
                var newRunningNumbers = runningNumbers.Skip(existingActiveLogs.Count).ToList();

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
                _context.Logs.AddRange(newLogs);

                // Save changes for both updates and new inserts
                await _context.SaveChangesAsync();

                // Return the requested running numbers
                return Ok(runningNumbers);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message, stackTrace = ex.StackTrace });
            }
        }

        // Utility method to get the next value from the sequence
        private async Task<long> GetNextSequenceValueAsync(string SequenceName)
        {
            var connection = _context.Database.GetDbConnection();
            await connection.OpenAsync();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT NEXT VALUE FOR dbo.{SequenceName};";
                var result = await command.ExecuteScalarAsync();
                await connection.CloseAsync();
                return Convert.ToInt64(result);
            }
        }

    }
}
