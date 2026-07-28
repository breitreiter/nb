using System;
using System.Collections.Generic;

namespace nb.Shell
{
    /// <summary>
    /// The files nb itself writes into whatever directory it is run from.
    /// </summary>
    /// <remarks>
    /// These are an artifact of the observation, not part of the user's project,
    /// so the discovery tools filter them out — see <see cref="All"/>.
    ///
    /// The components that create these files reference the same constants, so a
    /// rename cannot leave the filter matching a name nothing writes any more.
    /// </remarks>
    public static class NbStateFiles
    {
        public const string ConversationHistory = ".nb_conversation_history.json";
        public const string ConversationHistoryLock = ".nb_conversation_history.lock";
        public const string ActiveKits = ".nb_active_kits.json";

        /// <summary>
        /// Names excluded by <c>find_files</c> and <c>list_dir</c>. Shared by both so
        /// the two tools cannot drift apart.
        /// </summary>
        public static readonly HashSet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ConversationHistory,
            ConversationHistoryLock,
            ActiveKits
        };
    }
}
