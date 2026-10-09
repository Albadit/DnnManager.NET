using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public enum IisServerState { Running, Stopped, Starting, Stopping, NotInstalled, Unknown }
