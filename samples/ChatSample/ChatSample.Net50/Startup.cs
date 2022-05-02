using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;
using System.Security.Claims;

namespace ChatSample.Net50
{
    public class FakeAuthenticationOptions : AuthenticationSchemeOptions
    {

    }

    public class FakeAuthenticationhandler : AuthenticationHandler<FakeAuthenticationOptions>
    {
        public FakeAuthenticationhandler(IOptionsMonitor<FakeAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("claimthatexists", "true")
            })), "Fake"))); ;
        }
    }

    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        private bool shouldUseAzureSignalR => Configuration.GetValue<bool>("ShouldUseAzureSignalR");

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            var signalRBuilder = services
                .AddSignalR();

            if (shouldUseAzureSignalR)
            {
                signalRBuilder.AddAzureSignalR();
            }

            signalRBuilder.AddMessagePackProtocol()
                .Services
                .AddAuthentication(opts => opts.DefaultAuthenticateScheme = opts.DefaultChallengeScheme = "Fake")
                    .AddScheme<FakeAuthenticationOptions, FakeAuthenticationhandler>("Fake", options =>
                    {

                    })
                .Services
                .AddAuthorization(opts =>
                {
                    opts.AddPolicy("FooBar", builder =>
                    {
                        builder.RequireClaim("claimthatexists");
                    });

                    var fallbackPolicy = new AuthorizationPolicyBuilder().RequireClaim("claimthatdoesnotexist").Build();

                    opts.FallbackPolicy = fallbackPolicy;
                });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseFileServer();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            if (shouldUseAzureSignalR)
            {
                app.UseAzureSignalR(routes =>
                {
                    routes.MapHub<Chat>("/chat");
                    routes.MapHub<BenchHub>("/signalrbench");
                });
            }
            else
            {
                app.UseEndpoints(routes =>
                {
                    routes.MapHub<Chat>("/chat");
                    routes.MapHub<BenchHub>("/signalrbench");
                });
            }
        }
    }
}
