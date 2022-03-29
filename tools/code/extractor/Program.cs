﻿using Azure.Core;
using Azure.Identity;
using common;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Extensions.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace extractor;

public static class Program
{
    public static async Task Main(string[] arguments)
    {
        await CreateBuilder(arguments).Build().RunAsync();
    }

    private static IHostBuilder CreateBuilder(string[] arguments)
    {
        return Host
            .CreateDefaultBuilder(arguments)
            .ConfigureAppConfiguration(ConfigureConfiguration)
            .ConfigureServices(ConfigureServices);
    }

    private static void ConfigureConfiguration(IConfigurationBuilder builder)
    {
        builder.AddUserSecrets(typeof(Program).Assembly);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(GetAzureEnvironment)
                .AddTransient(GetTokenCredential)
                .AddSingleton<AzureHttpClient>()
                .ConfigureHttp()
                .AddHostedService<Extractor>();
    }

    private static AzureEnvironment GetAzureEnvironment(IServiceProvider provider) =>
        provider.GetRequiredService<IConfiguration>().TryGetValue("AZURE_CLOUD_ENVIRONMENT") switch
        {
            null => AzureEnvironment.AzureGlobalCloud,
            nameof(AzureEnvironment.AzureGlobalCloud) => AzureEnvironment.AzureGlobalCloud,
            nameof(AzureEnvironment.AzureChinaCloud) => AzureEnvironment.AzureChinaCloud,
            nameof(AzureEnvironment.AzureUSGovernment) => AzureEnvironment.AzureUSGovernment,
            nameof(AzureEnvironment.AzureGermanCloud) => AzureEnvironment.AzureGermanCloud,
            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(AzureEnvironment.AzureGlobalCloud)}, {nameof(AzureEnvironment.AzureChinaCloud)}, {nameof(AzureEnvironment.AzureUSGovernment)}, {nameof(AzureEnvironment.AzureGermanCloud)}")
        };

    private static TokenCredential GetTokenCredential(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var token = configuration.TryGetValue("AZURE_BEARER_TOKEN");

        if (token is null)
        {
            var environment = provider.GetRequiredService<AzureEnvironment>();
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                AuthorityHost = new Uri(environment.AuthenticationEndpoint)
            });
        }
        else
        {
            return new StaticTokenCredential(token);
        }
    }

    private static IServiceCollection ConfigureHttp(this IServiceCollection services)
    {
        var getRetryDuration = (int retryCount) =>
            Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromMilliseconds(500), retryCount, fastFirst: true)
                   .Last();

        var retryOnTimeoutPolicy =
            HttpPolicyExtensions.HandleTransientHttpError()
                                .OrResult(response => response.StatusCode is HttpStatusCode.TooManyRequests)
                                .WaitAndRetryAsync(10, getRetryDuration);

        services.AddHttpClient<NonAuthenticatedHttpClient>()
                .AddPolicyHandler(retryOnTimeoutPolicy);

        return services;
    }
}
