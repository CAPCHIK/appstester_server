using System;
using AppsTester.Controller.Files;
using AppsTester.Controller.Moodle;
using AppsTester.Controller.Services;
using AppsTester.Controller.Submissions;
using AppsTester.Shared.RabbitMq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppsTester.Controller
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();

            services.AddRabbitMq();

            services.AddSingleton(_ => new FileCache("apps-tester.controller", TimeSpan.FromDays(7)));
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection")));

            services.AddHttpClient();

            services.AddTransient<IMoodleCommunicator, MoodleCommunicator>();

            services.AddHostedService<SubscriptionCheckResultsProcessor>();
            services.AddHostedService<SubscriptionCheckStatusesProcessor>();
            services.AddHostedService<SubmissionsInfoSynchronizer>();
            services.AddSingleton<IMoodleService, MoodleService>();
            services.AddSignalR();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}