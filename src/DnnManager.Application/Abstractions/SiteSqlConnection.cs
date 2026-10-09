using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public sealed record SiteSqlConnection(string Server, string Database, string User, string Password);
