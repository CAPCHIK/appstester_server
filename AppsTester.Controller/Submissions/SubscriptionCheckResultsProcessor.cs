using System;
using System.Threading;
using System.Threading.Tasks;
using AppsTester.Controller.Services;
using AppsTester.Shared.RabbitMq;
using AppsTester.Shared.SubmissionChecker.Events;
using EasyNetQ;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppsTester.Controller.Submissions
{
    internal class SubscriptionCheckResultsProcessor : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IRabbitBusProvider _rabbitBusProvider;
        private readonly IMoodleService _moodleService;
        private readonly ILogger<SubscriptionCheckResultsProcessor> _logger;

        public SubscriptionCheckResultsProcessor(
            IServiceScopeFactory serviceScopeFactory,
            IRabbitBusProvider rabbitBusProvider,
            IMoodleService moodleService,
            ILogger<SubscriptionCheckResultsProcessor> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _rabbitBusProvider = rabbitBusProvider;
            _moodleService = moodleService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var rabbitConnection = _rabbitBusProvider.GetRabbitBus();

            await rabbitConnection
                .PubSub
                .SubscribeAsync<SubmissionCheckResultEvent>(subscriptionId: "", onMessage: async resultEvent =>
                    {
                        try
                        {
                            using var serviceScope = _serviceScopeFactory.CreateScope();

                            var applicationDbContext =
                                serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                            var subscriptionCheck =
                                await applicationDbContext.SubmissionChecks.FirstOrDefaultAsync(
                                    s => s.Id == resultEvent.SubmissionId, cancellationToken: stoppingToken);
                            if (subscriptionCheck == null)
                                throw new InvalidOperationException();

                            subscriptionCheck.SerializedResult = resultEvent.SerializedResult;
                            await applicationDbContext.SaveChangesAsync(stoppingToken);
                            await _moodleService.SetSubmissionResultAsync(subscriptionCheck.AttemptStepId,
                                resultEvent.SerializedResult, stoppingToken);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, "can't handle submissionresult {SubmissionId}", resultEvent.SubmissionId);

                            await rabbitConnection
                                .Scheduler
                                .FuturePublishAsync(resultEvent, TimeSpan.FromMinutes(1), stoppingToken);
                        }
                    }, cancellationToken: stoppingToken);
        }
    }
}