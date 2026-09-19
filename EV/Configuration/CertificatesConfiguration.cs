/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of EV <https://github.com/OpenChargingCloud/EV>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.EV.Configuration
{

    /// <summary>
    /// The "certificates" section of the configuration file: where this
    /// vehicle's certificate store is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One field, and that is the whole point of it. <b>What is in the store is
    /// not configuration</b> - it is the content of a directory, and it is
    /// described by that directory's own index. So this section says where to
    /// look and nothing else, and a store copied to another machine arrives
    /// complete rather than needing half of itself written out of somebody's
    /// configuration file.
    /// </para>
    /// <para>
    /// Which certificate a session uses is not here either. That is a choice
    /// about one session, it lives in
    /// <see cref="SessionConfiguration"/> as a handle into this store, and it
    /// changes far more often than the store does.
    /// </para>
    /// </remarks>
    /// <param name="Directory">Where the certificates are kept.</param>
    public sealed record CertificatesConfiguration(String? Directory = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName      = "certificates";

        /// <summary>
        /// Where the store is when nobody says otherwise: beside the accounts,
        /// below the repository root.
        /// </summary>
        public const String  DefaultDirectory = "certificates";

        /// <summary>
        /// The longest a path may be written.
        /// </summary>
        public const Int32   MaxPathLength    = 4096;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "certificates" section, or the one sentence that says what is
        /// wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                             JSON,
                                       [NotNullWhen(true)]  out CertificatesConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?                     Error)
        {

            Configuration  = null;
            Error          = null;

            if (!ConfigurationReader.TryReadString(JSON, "directory", SectionName, MaxPathLength, out var directory, out Error))
                return false;

            Configuration = new CertificatesConfiguration(directory);

            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Directory is not null)
                json.Add("directory", Directory);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"certificates in {Directory ?? DefaultDirectory}";

        #endregion

    }

}
