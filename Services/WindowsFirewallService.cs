using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

/// <summary>Creates and inspects persistent Windows Firewall rules for managed game servers.</summary>
public sealed class WindowsFirewallService(ILogger<WindowsFirewallService> logger)
{
    private const int PrivateNetworkProfile = 0x2;
    private const int DomainNetworkProfile = 0x1;
    private const int PublicNetworkProfile = 0x4;
    private const int AllNetworkProfiles = 0x7fffffff;
    private const int InboundDirection = 1;
    private const int AllowAction = 1;

    public bool IsRunningAsAdministrator => OperatingSystem.IsWindows() && IsAdministrator();

    public static string GetLocalPort(ServerProfile profile, IGameAdapter adapter)
    {
        var lastPort = checked(profile.Port + adapter.PortSpan - 1);
        return adapter.PortSpan == 1 ? profile.Port.ToString() : $"{profile.Port}-{lastPort}";
    }

    public static FirewallRuleDefinition CreateRuleDefinition(ServerProfile profile, IGameAdapter adapter)
    {
        var executablePath = Path.Combine(Path.GetFullPath(profile.InstallPath), adapter.ExecutableName);
        return new($"DSS-{profile.Id:N}", $"DSS · {profile.ServerName}", executablePath,
            adapter.FirewallProtocol, GetLocalPort(profile, adapter));
    }

    public Task<(bool Success, string Message, int RulesApplied)> ConfigureRulesAsync(
        IReadOnlyList<FirewallRuleDefinition> rules, bool includePublicNetworks)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult((false, "A configuração automática do Firewall do Windows só está disponível no Windows.", 0));
        if (!IsRunningAsAdministrator)
            return Task.FromResult((false, "O DSS precisa estar executando como administrador para configurar a rede deste servidor. Reinicie o painel com a opção de administrador e tente novamente.", 0));
        if (rules.Count == 0)
            return Task.FromResult((false, "Nenhum servidor tem uma pasta de instalação configurada para autorizar.", 0));

        return RunOnStaThreadAsync(() =>
        {
            object? policyObject = null;
            object? rulesObject = null;
            try
            {
                policyObject = CreateFirewallComObject("HNetCfg.FwPolicy2");
                dynamic policy = policyObject;
                rulesObject = policy.Rules;
                dynamic firewallRules = rulesObject;
                var profileMask = includePublicNetworks
                    ? DomainNetworkProfile | PrivateNetworkProfile | PublicNetworkProfile
                    : PrivateNetworkProfile;
                var applied = 0;
                var failures = new List<string>();

                foreach (var definition in rules)
                {
                    object? ruleObject = null;
                    try
                    {
                        ruleObject = CreateFirewallComObject("HNetCfg.FWRule");
                        dynamic rule = ruleObject;
                        rule.Name = definition.Name;
                        rule.Description = "Regra de entrada criada pelo Dedicated Server Studio.";
                        rule.Grouping = "Dedicated Server Studio";
                        rule.ApplicationName = Path.GetFullPath(definition.ExecutablePath);
                        rule.Protocol = ProtocolNumber(definition.Protocol);
                        rule.LocalPorts = definition.LocalPort;
                        rule.Direction = InboundDirection;
                        rule.Enabled = true;
                        rule.Action = AllowAction;
                        rule.Profiles = profileMask;
                        rule.InterfaceTypes = "All";
                        rule.EdgeTraversal = false;

                        firewallRules.Remove(definition.Name);
                        firewallRules.Add(ruleObject);
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{definition.DisplayName}: {ex.Message}");
                    }
                    finally
                    {
                        ReleaseComObject(ruleObject);
                    }
                }

                if (failures.Count > 0)
                {
                    var detail = string.Join(Environment.NewLine, failures);
                    logger.LogWarning("Could not configure {AppliedRuleCount} of {RequestedRuleCount} Windows Firewall rules: {FirewallError}", applied, rules.Count, detail);
                    return (false, $"O DSS configurou {applied} de {rules.Count} regra(s). {detail}", applied);
                }

                return (true, $"Acesso de rede liberado para {applied} servidor(es).", applied);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not access Windows Firewall configuration");
                return (false, "O DSS não conseguiu salvar as regras do Firewall. " + ex.Message, 0);
            }
            finally
            {
                ReleaseComObject(rulesObject);
                ReleaseComObject(policyObject);
            }
        });
    }

    public async Task<(bool Success, string Message)> EnsurePrivateNetworkRuleAsync(
        ServerProfile profile,
        IGameAdapter adapter,
        string executablePath)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "A configuração automática do Firewall do Windows só está disponível no Windows.");
        if (!IsRunningAsAdministrator)
            return (false, "O DSS precisa estar executando como administrador para configurar a rede deste servidor. Reinicie o painel com a opção de administrador e tente novamente.");

        var rule = new FirewallRuleDefinition($"DSS-{profile.Id:N}", $"DSS · {profile.ServerName}", Path.GetFullPath(executablePath),
            adapter.FirewallProtocol, GetLocalPort(profile, adapter));
        var configured = await ConfigureRulesAsync([rule], includePublicNetworks: false);
        return (configured.Success, configured.Message);
    }

    public Task<bool> IsPanelPortAuthorizedAsync(int port)
    {
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535) return Task.FromResult(false);
        var ruleName = PanelRuleName(port);
        return RunOnStaThreadAsync(() =>
        {
            object? policyObject = null;
            object? rulesObject = null;
            object? ruleObject = null;
            try
            {
                policyObject = CreateFirewallComObject("HNetCfg.FwPolicy2");
                dynamic policy = policyObject;
                rulesObject = policy.Rules;
                dynamic rules = rulesObject;
                try { ruleObject = rules.Item(ruleName); }
                catch (COMException) { return false; }
                dynamic rule = ruleObject;
                var profiles = Convert.ToInt32((object)rule.Profiles, System.Globalization.CultureInfo.InvariantCulture);
                var localPorts = Convert.ToString((object)rule.LocalPorts, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                var remoteAddresses = Convert.ToString((object)rule.RemoteAddresses, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                var enabled = Convert.ToBoolean((object)rule.Enabled, System.Globalization.CultureInfo.InvariantCulture);
                var direction = Convert.ToInt32((object)rule.Direction, System.Globalization.CultureInfo.InvariantCulture);
                var action = Convert.ToInt32((object)rule.Action, System.Globalization.CultureInfo.InvariantCulture);
                var protocol = Convert.ToInt32((object)rule.Protocol, System.Globalization.CultureInfo.InvariantCulture);
                var privateAndDomain = PrivateNetworkProfile | DomainNetworkProfile;
                return enabled && direction == InboundDirection && action == AllowAction && protocol == 6 &&
                    (profiles & privateAndDomain) == privateAndDomain &&
                    localPorts == port.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                    string.Equals(remoteAddresses, "LocalSubnet", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not inspect the DSS panel firewall rule for port {Port}", port);
                return false;
            }
            finally
            {
                ReleaseComObject(ruleObject);
                ReleaseComObject(rulesObject);
                ReleaseComObject(policyObject);
            }
        });
    }

    public Task<(bool Success, string Message)> ConfigurePanelPortAsync(int port, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult((false, "A regra do painel só pode ser alterada no Firewall do Windows."));
        if (port is < 1 or > 65535) return Task.FromResult((false, "A porta configurada para o painel é inválida."));
        if (!IsRunningAsAdministrator) return Task.FromResult((false, "Inicie o DSS como administrador para alterar o Firewall do Windows."));
        var ruleName = PanelRuleName(port);
        return RunOnStaThreadAsync(() =>
        {
            object? policyObject = null;
            object? rulesObject = null;
            object? ruleObject = null;
            try
            {
                policyObject = CreateFirewallComObject("HNetCfg.FwPolicy2");
                dynamic policy = policyObject;
                rulesObject = policy.Rules;
                dynamic rules = rulesObject;
                if (!enabled)
                {
                    try { rules.Remove(ruleName); }
                    catch (COMException) { }
                    return (true, "A regra de acesso do painel foi removida.");
                }

                ruleObject = CreateFirewallComObject("HNetCfg.FWRule");
                dynamic rule = ruleObject;
                rule.Name = ruleName;
                rule.Description = "Permite acesso ao painel DSS apenas pela sub-rede local.";
                rule.Grouping = "Dedicated Server Studio";
                rule.Protocol = 6;
                rule.LocalPorts = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                rule.RemoteAddresses = "LocalSubnet";
                rule.Direction = InboundDirection;
                rule.Enabled = true;
                rule.Action = AllowAction;
                rule.Profiles = PrivateNetworkProfile | DomainNetworkProfile;
                rule.InterfaceTypes = "All";
                rule.EdgeTraversal = false;
                try { rules.Remove(ruleName); }
                catch (COMException) { }
                rules.Add(ruleObject);
                return (true, $"A porta TCP {port} foi liberada no Firewall somente para a rede local.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not configure DSS panel firewall rule on port {Port}", port);
                return (false, "O Windows não conseguiu atualizar a regra do Firewall. " + ex.Message);
            }
            finally
            {
                ReleaseComObject(ruleObject);
                ReleaseComObject(rulesObject);
                ReleaseComObject(policyObject);
            }
        });
    }

    private static string PanelRuleName(int port) => $"DSS-Panel-TCP-{port}";

    public Task<bool> IsAuthorizedAsync(ServerProfile profile, IGameAdapter adapter, string executablePath)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(false);

        var expectedExecutable = Path.GetFullPath(executablePath);
        var expectedPort = GetLocalPort(profile, adapter);
        var expectedProtocol = ProtocolNumber(adapter.FirewallProtocol);
        var ruleName = $"DSS-{profile.Id:N}";

        return RunOnStaThreadAsync(() =>
        {
            object? policyObject = null;
            object? rulesObject = null;
            object? ruleObject = null;
            try
            {
                policyObject = CreateFirewallComObject("HNetCfg.FwPolicy2");
                dynamic policy = policyObject;
                rulesObject = policy.Rules;
                dynamic rules = rulesObject;
                try { ruleObject = rules.Item(ruleName); }
                catch (COMException) { return false; }

                dynamic rule = ruleObject;
                var profiles = Convert.ToInt32((object)rule.Profiles, System.Globalization.CultureInfo.InvariantCulture);
                var application = Convert.ToString((object)rule.ApplicationName, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                var localPorts = Convert.ToString((object)rule.LocalPorts, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                var enabled = Convert.ToBoolean((object)rule.Enabled, System.Globalization.CultureInfo.InvariantCulture);
                var direction = Convert.ToInt32((object)rule.Direction, System.Globalization.CultureInfo.InvariantCulture);
                var action = Convert.ToInt32((object)rule.Action, System.Globalization.CultureInfo.InvariantCulture);
                var protocol = Convert.ToInt32((object)rule.Protocol, System.Globalization.CultureInfo.InvariantCulture);
                return enabled &&
                    direction == InboundDirection &&
                    action == AllowAction &&
                    (profiles == AllNetworkProfiles || (profiles & PrivateNetworkProfile) != 0) &&
                    string.Equals(Path.GetFullPath(application), expectedExecutable, StringComparison.OrdinalIgnoreCase) &&
                    protocol == expectedProtocol &&
                    string.Equals(localPorts, expectedPort, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not inspect Windows Firewall rule {RuleName}", ruleName);
                return false;
            }
            finally
            {
                ReleaseComObject(ruleObject);
                ReleaseComObject(rulesObject);
                ReleaseComObject(policyObject);
            }
        });
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static object CreateFirewallComObject(string programmaticId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A API do Firewall do Windows só está disponível no Windows.");
        var type = Type.GetTypeFromProgID(programmaticId, throwOnError: true)!;
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Não foi possível iniciar o componente do Firewall {programmaticId}.");
    }

    private static int ProtocolNumber(string protocol) => protocol.Trim().ToUpperInvariant() switch
    {
        "TCP" => 6,
        "UDP" => 17,
        "ICMPV4" => 1,
        "ICMPV6" => 58,
        "ANY" => 256,
        _ when int.TryParse(protocol, out var value) => value,
        _ => throw new ArgumentException($"Protocolo de Firewall não reconhecido: {protocol}", nameof(protocol))
    };

    private static void ReleaseComObject(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }

    private static Task<T> RunOnStaThreadAsync<T>(Func<T> operation)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromException<T>(new PlatformNotSupportedException("A API do Firewall do Windows só está disponível no Windows."));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(operation()); }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "DSS Windows Firewall"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

public sealed record FirewallRuleDefinition(string Name, string DisplayName, string ExecutablePath, string Protocol, string LocalPort);
