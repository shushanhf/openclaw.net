using Microsoft.Extensions.Logging;
using OpenClaw.PluginKit;
using OpenClaw.Plugins.TokenJuice.Reduction;
using OpenClaw.Plugins.TokenJuice.Rules;

namespace OpenClaw.Plugins.TokenJuice;

public sealed class TokenJuicePlugin : INativeDynamicPlugin
{
    public void Register(INativeDynamicPluginContext context)
    {
        var rules = RuleLoader.LoadMergedRules();
        var interceptor = new TokenJuiceInterceptor(rules);

        context.Logger.LogInformation(
            "TokenJuice: loaded {Count} rules from builtin + user + project sources",
            rules.Count);

        context.RegisterResultInterceptor(interceptor);
    }
}
