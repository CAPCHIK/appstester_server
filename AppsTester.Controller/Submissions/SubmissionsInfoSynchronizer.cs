using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppsTester.Controller.Files;
using AppsTester.Controller.Moodle;
using AppsTester.Shared.RabbitMq;
using AppsTester.Shared.SubmissionChecker.Events;
using EasyNetQ;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Sentry;

namespace AppsTester.Controller.Submissions
{
    internal class SubmissionsInfoSynchronizer : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IRabbitBusProvider _rabbitBusProvider;
        private readonly IMoodleCommunicator _moodleCommunicator;

        public SubmissionsInfoSynchronizer(
            IServiceScopeFactory serviceScopeFactory,
            IRabbitBusProvider rabbitBusProvider,
            IMoodleCommunicator moodleCommunicator)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _rabbitBusProvider = rabbitBusProvider;
            _moodleCommunicator = moodleCommunicator;
        }
        
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (true)
            {
                try
                {
                    var attemptAndStepIds = await _moodleCommunicator.GetFunctionResultAsync<Dictionary<int, int[]>>(
                        functionName: "local_qtype_get_submissions_to_check", cancellationToken: stoppingToken);

                    if (attemptAndStepIds == null || !attemptAndStepIds.Any())
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                        continue;
                    }

                    using var serviceScope = _serviceScopeFactory.CreateScope();
                    await using var dbContext = serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    foreach (var attemptId in attemptAndStepIds.Keys)
                    {
                        var lastSendingDateTime = await dbContext.SubmissionChecks
                            .Where(x => x.AttemptId == attemptId)
                            .Select(x => x.SendingDateTimeUtc)
                            .OrderByDescending(x => x)
                            .FirstOrDefaultAsync(stoppingToken);
                        foreach (var attemptStepId in attemptAndStepIds[attemptId])
                        {
                            try
                            {
                                var sc = await dbContext.SubmissionChecks
                                    .Where(x => x.AttemptStepId == attemptStepId)
                                    .FirstOrDefaultAsync(stoppingToken);

                                if (sc is not null && sc.SendingDateTimeUtc != lastSendingDateTime)
                                {
                                    await _moodleCommunicator.CallFunctionAsync(
                                        functionName: "local_qtype_set_submission_status",
                                        functionParams: new Dictionary<string, object>
                                        {
                                            ["id"] = sc.AttemptStepId
                                        },
                                        requestParams: new Dictionary<string, string>
                                        {
                                            ["status"] = sc.LastSerializedStatus
                                        },
                                        cancellationToken: stoppingToken);

                                    await _moodleCommunicator.CallFunctionAsync(
                                        functionName: "local_qtype_set_submission_results",
                                        functionParams: new Dictionary<string, object>
                                        {
                                            ["id"] = sc.AttemptStepId
                                        },
                                        requestParams: new Dictionary<string, string>
                                        {
                                            ["result"] = sc.SerializedResult
                                        },
                                        cancellationToken: stoppingToken);
                                }
                                else if (sc is null || sc.SendingDateTimeUtc == lastSendingDateTime &&
                                         sc.SerializedResult is not null)
                                {
                                    var submission = await _moodleCommunicator.GetFunctionResultAsync<Submission>(
                                        functionName: "local_qtype_get_submission",
                                        functionParams: new Dictionary<string, object> { ["id"] = attemptId },
                                        cancellationToken: stoppingToken);

                                    var fileCache = serviceScope.ServiceProvider.GetRequiredService<FileCache>();

                                    var missingFiles = submission
                                        .Files
                                        .Where(pair => pair.Key.EndsWith("_hash"))
                                        .Where(pair => !fileCache.IsKeyExists(pair.Value))
                                        .ToList();

                                    if (missingFiles.Any())
                                    {
                                        submission = await _moodleCommunicator.GetFunctionResultAsync<Submission>(
                                            functionName: "local_qtype_get_submission",
                                            functionParams: new Dictionary<string, object>
                                            {
                                                ["id"] = attemptId,
                                                ["included_file_hashes"] = string.Join(",",
                                                    missingFiles.Select(mf => mf.Value))
                                            },
                                            cancellationToken: stoppingToken);

                                        foreach (var (fileName, fileHash) in missingFiles)
                                            fileCache.Write(fileHash,
                                                Convert.FromBase64String(
                                                    submission.Files[fileName.Substring(0, fileName.Length - 5)]));
                                    }

                                    var submissionId = Guid.NewGuid();
                                    var submissionCheckRequest = new SubmissionCheckRequestEvent
                                    {
                                        SubmissionId = submissionId,
                                        Files = submission.Files.Where(f => f.Key.EndsWith("_hash"))
                                            .ToDictionary(pair => pair.Key.Substring(0, pair.Key.Length - 5),
                                                pair => pair.Value),
                                        PlainParameters = submission.Parameters
                                    };
                                    var submissionCheck = new SubmissionCheck
                                    {
                                        Id = submissionId,
                                        AttemptStepId = attemptId,
                                        SendingDateTimeUtc = DateTime.UtcNow,
                                        SerializedRequest = JsonConvert.SerializeObject(submissionCheckRequest)
                                    };
                                    dbContext.SubmissionChecks.Add(submissionCheck);

                                    var rabbitConnection = _rabbitBusProvider.GetRabbitBus();
                                    await rabbitConnection.PubSub.PublishAsync(submissionCheckRequest,
                                        topic: submission.CheckerSystemName,
                                        cancellationToken: stoppingToken);

                                    await dbContext.SaveChangesAsync(stoppingToken);
                                }
                            }
                            catch (Exception e)
                            {
                                SentrySdk.CaptureException(e);
                            }
                            finally
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                            }
                        }                        
                    }
                }
                catch (Exception e)
                {
                    SentrySdk.CaptureException(e);
                }
                finally
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }
        
    }
}