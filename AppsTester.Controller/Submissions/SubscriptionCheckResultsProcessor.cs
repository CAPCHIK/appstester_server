using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AppsTester.Controller.Moodle;
using AppsTester.Controller.Services;
using AppsTester.Shared.RabbitMq;
using AppsTester.Shared.SubmissionChecker.Events;
using EasyNetQ;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentry;

namespace AppsTester.Controller.Submissions
{
    internal class SubscriptionCheckResultsProcessor : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IRabbitBusProvider _rabbitBusProvider;
        private readonly IMoodleService _moodleService;

        public SubscriptionCheckResultsProcessor(
            IServiceScopeFactory serviceScopeFactory,
            IRabbitBusProvider rabbitBusProvider,
            IMoodleService moodleService)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _rabbitBusProvider = rabbitBusProvider;
            _moodleService = moodleService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var rabbitConnection = _rabbitBusProvider.GetRabbitBus();

            await rabbitConnection
                .PubSub
                .SubscribeAsync<SubmissionCheckResultEvent>(
                    subscriptionId: "",
                    onMessage: async resultEvent =>
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
                            SentrySdk.CaptureException(e);

                            await rabbitConnection
                                .Scheduler
                                .FuturePublishAsync(resultEvent, TimeSpan.FromMinutes(1), stoppingToken);
                        }
                    });
        }
    }
}