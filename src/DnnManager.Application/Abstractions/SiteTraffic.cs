using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>Bytes a site received and sent over HTTP since IIS started.</summary>
public sealed record SiteTraffic(long BytesReceived, long BytesSent);
