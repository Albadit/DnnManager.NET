using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <param name="Debug">&lt;compilation debug&gt;; null when not set.</param>
/// <param name="DisabledHttpsRules">HTTPS redirect rules DNN Manager switched off for local development.</param>
public sealed record WebConfigFacts(bool? Debug, string? TargetFramework, string? CustomErrors, IReadOnlyList<string> DisabledHttpsRules);
