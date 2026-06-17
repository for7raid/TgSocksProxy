using Microsoft.Extensions.Logging;
using System.Net;
using TgSocksProxy;

Console.WriteLine("Hello, World!");

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddDebug();
    builder.AddConsole();
});


var proxy = new SocksProxy(new SocksProxyOptions
{
    //LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 10180),
    //UpstreamProxyEndPoint = new DnsEndPoint("", 443),
    //UpstreamCredentials = new ProxyCredentials("", ""),
    //UseTls = true,
    Logger = loggerFactory.CreateLogger<SocksProxy>()
});

proxy.Start();

Console.ReadLine();