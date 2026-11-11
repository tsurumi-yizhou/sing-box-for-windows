using Grpc.Core;

namespace SFW.Services.Core;

/// <summary>
/// Declares the app's UI language on every daemon RPC. The daemon localizes
/// user-visible text per call from the accept-language metadata and has no
/// connection-level default, so a call that omits it answers in the daemon's
/// own language while the app renders in another one. Wrapping the channel
/// keeps that declaration in one place instead of at every call site.
/// </summary>
internal sealed class LocaleCallInvoker : CallInvoker
{
    private readonly CallInvoker _inner;

    internal LocaleCallInvoker(CallInvoker inner) => _inner = inner;

    private static CallOptions WithLocale(CallOptions options)
    {
        var headers = options.Headers;
        if (headers is not null &&
            headers.Any(item => string.Equals(item.Key, "accept-language", StringComparison.OrdinalIgnoreCase)))
        {
            return options;
        }

        headers ??= new Metadata();
        headers.Add("accept-language", BoxddCoreController.CurrentDaemonLocale());
        return options.WithHeaders(headers);
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        where TRequest : class
        where TResponse : class =>
        _inner.AsyncUnaryCall(method, host, WithLocale(options), request);

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        where TRequest : class
        where TResponse : class =>
        _inner.AsyncClientStreamingCall(method, host, WithLocale(options));

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        where TRequest : class
        where TResponse : class =>
        _inner.AsyncServerStreamingCall(method, host, WithLocale(options), request);

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        where TRequest : class
        where TResponse : class =>
        _inner.AsyncDuplexStreamingCall(method, host, WithLocale(options));

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        where TRequest : class
        where TResponse : class =>
        _inner.BlockingUnaryCall(method, host, WithLocale(options), request);
}
