using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.Client.DependencyInjection;

internal sealed class MrWhoOidcLoggingFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        return builder =>
        {
            next(builder);

            var loggerFactory = builder.Services.GetService<ILoggerFactory>();
            if (loggerFactory is null)
            {
                return;
            }

            var logger = loggerFactory.CreateLogger("MrWhoOidc.Client.HttpHandler");
            var innerHandler = builder.PrimaryHandler;
            builder.PrimaryHandler = new LoggingHandler(innerHandler, logger);
        };
    }

    private sealed class LoggingHandler : DelegatingHandler
    {
        private readonly ILogger _logger;

        public LoggingHandler(HttpMessageHandler innerHandler, ILogger logger)
            : base(innerHandler)
        {
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var start = DateTimeOffset.UtcNow;
            _logger.LogDebug("Sending {Method} {Uri}", request.Method, request.RequestUri);

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var elapsed = DateTimeOffset.UtcNow - start;
            _logger.LogDebug("Completed {Method} {Uri} with {StatusCode} in {Elapsed}ms", request.Method, request.RequestUri, (int)response.StatusCode, elapsed.TotalMilliseconds);

            return response;
        }
    }
}
