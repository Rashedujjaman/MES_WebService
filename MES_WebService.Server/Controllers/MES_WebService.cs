using Microsoft.AspNetCore.Mvc;
using MES_WebService.Server.Data;
using MES_WebService.Server.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;



namespace MES_WebService.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MES_WebService : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly IRunningNumberService _runningNumberService;

        public MES_WebService(ApplicationDbContext dbContext, IRunningNumberService runningNumberService)
        {
            _dbContext = dbContext;
            _runningNumberService = runningNumberService;
        }


        #region Main Function
        [HttpGet("GetRunningNumbers")]
        public async Task<IActionResult> GetRunningNumbers(string SequenceName, int RunNoCount, string LotId, int StepNo, string Factory, bool IsUnique)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
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
                        var nextValue = await GetNextSequenceValueAsync(SequenceName, StepNo);
                        if (nextValue == 0)
                        {
                            return BadRequest($"Invalid Sequence Name : {SequenceName}");
                        }
                        else if (nextValue == -1)
                        {
                            return BadRequest($"Invalid Step No : {StepNo}");
                        }
                        newRunningNumbers.Add(nextValue);

                        var generatedNumber = await _runningNumberService.GenerateRunningNumberAsync(Factory, LotId, nextValue);
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
                            _dbContext.Logs.UpdateRange(existingActiveLogs);
                        }


                        foreach (var log in existingInactiveLogs)
                        {
                            log.DeleteFlag = true;
                        }

                        if (existingInactiveLogs.Count > 0)
                        {
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
                                _dbContext.Logs.UpdateRange(existingActiveLogs);
                            }
                        }

                        // If the existing logs are less than the requested running numbers,
                        // then get the remaining running numbers from the sequence.
                        var requiredRunNumbers = RunNoCount - existingActiveLogs.Count;

                        for (int i = 0; i < requiredRunNumbers; i++)
                        {
                            var nextValue = await GetNextSequenceValueAsync(SequenceName, StepNo);

                            if (nextValue == 0)
                            {
                                return BadRequest($"Invalid Sequence Name : {SequenceName}");
                            }
                            else if (nextValue == -1)
                            {
                                return BadRequest($"Invalid Step No : {StepNo}");
                            }

                            newRunningNumbers.Add(nextValue);


                            var generatedNumber = await _runningNumberService.GenerateRunningNumberAsync(Factory, LotId, nextValue);
                            generatedRunningNumbers.Add(generatedNumber);
                        }
                    }

                }

                // Check for duplicates between generated running numbers and existing logs for current lot id

                // Duiplicate check for same lot id
                var duplicateGeneratedRunningNumbers = generatedRunningNumbers
                    .Where(grn => existingLogs.Any(log => log.GeneratedRunningNumber == grn))
                    .ToList();

                // Duiplicate check for different lot id
                if (IsUnique && duplicateGeneratedRunningNumbers.Count ==0)
                {
                    var drn = await _dbContext.Logs
                        .Where(log => generatedRunningNumbers.Contains(log.GeneratedRunningNumber) &&
                                      log.LotId != LotId &&
                                      log.SequenceName == SequenceName)
                        .ToListAsync();
                    duplicateGeneratedRunningNumbers.AddRange(drn.Select(rn => rn.GeneratedRunningNumber).ToList());
                }

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
                    _dbContext.Logs.AddRange(newLogs);
                }

                await _dbContext.SaveChangesAsync();

                await transaction.CommitAsync();

                var totalRunningNumLogs = new List<Log>();
                totalRunningNumLogs.AddRange(existingActiveLogs);
                totalRunningNumLogs.AddRange(newLogs);

                // Return the requested generatedRunningNumbers
                var totalGeneratedRunningNumbers = totalRunningNumLogs.Select(log => log.GeneratedRunningNumber).ToList();
                return Ok(totalGeneratedRunningNumbers);

            }
            catch (Exception ex)
            {
                if (transaction.GetDbTransaction()?.Connection != null)
                {
                    await transaction.RollbackAsync();
                }

                if (ex is HttpRequestException httpEx)
                {
                    return BadRequest(new { message = "Network error while connecting to the cloud. Please check your internet connection.", details = httpEx.Message });
                }
                else if (ex is ApplicationException appEx)
                {
                    return BadRequest(new { message = appEx.Message, details = appEx.InnerException?.Message });
                }

                return BadRequest(new { message = "An unexpected error occured", details = ex.Message});
            }
        }


        private async Task<List<Log>> GetExistingActiveLogs(string LotId, string SequenceName) 
        { 
            return await _dbContext.Logs
                        .Where(log => log.LotId == LotId && log.SequenceName == SequenceName)
                        .ToListAsync();
        }
        #endregion

        #region Sequence Helper Function
        private async Task<long> GetNextSequenceValueAsync(string SequenceName, int StepNo)
        {
            var connection = _dbContext.Database.GetDbConnection();

            var currentTransaction = _dbContext.Database.CurrentTransaction;

            if (currentTransaction == null)
            {
                throw new InvalidOperationException("No active transaction is available.");
            }

            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = currentTransaction.GetDbTransaction();

                    var sequenceExists = await CheckSequenceExistsAsync(SequenceName, connection, currentTransaction);

                    if (!sequenceExists)
                    {
                        if (StepNo == 1)
                        {
                            return 0;
                        }
                        else if (StepNo == 2)
                        {
                            await CreateSequenceAsync(SequenceName, connection, currentTransaction);
                        }
                        else
                        {
                            return -1;
                        }
                    }

                    command.CommandText = $"SELECT NEXT VALUE FOR dbo.{SequenceName};";

                    var result = await command.ExecuteScalarAsync();

                    //await connection.CloseAsync();
                    return Convert.ToInt64(result);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(ex.Message);
            }
        }

        private async Task<bool> CheckSequenceExistsAsync(string SequenceName, DbConnection connection, IDbContextTransaction currentTransaction)
        {
            using (var command = connection.CreateCommand())
            {
                // Attach the current transaction to the command
                command.Transaction = currentTransaction.GetDbTransaction();
                command.CommandText = $"SELECT COUNT(*) FROM sys.sequences WHERE name = '{SequenceName}'";
                var count = (int)await command.ExecuteScalarAsync();
                return count > 0;
            }
        }

        private async Task CreateSequenceAsync(string SequenceName, DbConnection connection, IDbContextTransaction currentTransaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = currentTransaction.GetDbTransaction();
                //command.CommandText = $"CREATE SEQUENCE {SequenceName} START WITH 1 INCREMENT BY 1;";
                command.CommandText = $"CREATE SEQUENCE {SequenceName} " +
                       $"START WITH {1} " +
                       $"INCREMENT BY 1 " +
                       $"MINVALUE {1} " +
                       $"MAXVALUE {999999}";
                await command.ExecuteNonQueryAsync();
            }
        }
        #endregion
    }
}
