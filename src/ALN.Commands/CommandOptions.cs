namespace ALN.Commands;

/// <summary>
/// The few numbers the instruction system has opinions about.
/// </summary>
/// <param name="Prefix">
/// Two slashes by default. One slash is what a path looks like, what a URL contains and what half the bots in a group
/// already use; two is what nobody types by accident, which is the whole point — a message that starts with the prefix
/// is <b>answered</b>, never passed on to the model, so a false positive costs a person a real message.
/// </param>
/// <param name="PerSenderPerMinute">
/// A group of forty people with one bored member is the case this exists for. Over the limit the message is dropped
/// <b>in silence</b>: answering every extra message would make the limit the amplifier.
/// </param>
/// <param name="MaxHelpLines">A help listing longer than a phone screen is a wall, not help.</param>
/// <param name="Scripted">
/// Whether instructions a script registered may run at all. Off by default: the moment it is on, somebody else's code
/// sits in the one path a person's words take toward the system, and that is a deployment's decision to make out loud.
/// </param>
public sealed record CommandOptions(
    string Prefix = "//",
    int PerSenderPerMinute = 10,
    int MaxHelpLines = 12,
    bool Scripted = false)
{
    public const string PrefixVariable = "ALN_COMMANDS_PREFIX";
    public const string ScriptedVariable = "ALN_COMMANDS_SCRIPTED";

    /// <summary>
    /// Read from the environment, next to the other deployment switches. An empty or missing prefix falls back to the
    /// default rather than to "everything is an instruction", which is the direction that cannot be undone.
    /// </summary>
    public static CommandOptions FromEnvironment()
    {
        var prefix = Environment.GetEnvironmentVariable(PrefixVariable);
        var scripted = Environment.GetEnvironmentVariable(ScriptedVariable);
        return new CommandOptions(
            string.IsNullOrWhiteSpace(prefix) ? "//" : prefix,
            Scripted: string.Equals(scripted, "true", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(scripted, "1", StringComparison.Ordinal));
    }
}
