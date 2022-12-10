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
using Sentry;

namespace AppsTester.Controller.Submissions
{
    internal class SubscriptionCheckStatusesProcessor : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IRabbitBusProvider _rabbitBusProvider;
        private readonly SemaphoreSlim _semaphore = new(initialCount: 1);
        private readonly IMoodleService _moodleService;

        public SubscriptionCheckStatusesProcessor(
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
                .SubscribeAsync<SubmissionCheckStatusEvent>(
                    subscriptionId: "",
                    onMessage: async statusEvent =>
                    {
                        await _semaphore.WaitAsync(stoppingToken);

                        try
                        {
                            using var serviceScope = _serviceScopeFactory.CreateScope();

                            var applicationDbContext =
                                serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                            var subscriptionCheck =
                                await applicationDbContext.SubmissionChecks.FirstOrDefaultAsync(
                                    s => s.Id == statusEvent.SubmissionId, cancellationToken: stoppingToken);
                            if (subscriptionCheck == null)
                                throw new InvalidOperationException();

                            if (subscriptionCheck.LastStatusVersion > statusEvent.Version)
                                return;

                            subscriptionCheck.LastSerializedStatus = statusEvent.SerializedStatus;
                            subscriptionCheck.LastStatusVersion = statusEvent.Version;

                            await applicationDbContext.SaveChangesAsync(stoppingToken);

                            await _moodleService.SetSubmissionStatusAsync(subscriptionCheck.AttemptStepId,
                                statusEvent.SerializedStatus, stoppingToken);
                        }
                        catch (Exception e)
                        {
                            SentrySdk.CaptureException(e);

                            await rabbitConnection
                                .Scheduler
                                .FuturePublishAsync(statusEvent, TimeSpan.FromMinutes(1), stoppingToken);
                        }
                        finally
                        {
                            _semaphore.Release();
                        }
                    });
        }
    }
}