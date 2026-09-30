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

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What the vehicle's code says is held to what the code of every kind of
    /// node is held to: SourceRules of WWCP_Node_TestKit, as the vehicle's
    /// pages are held to the node's Frontend/test/pages.ts.
    /// </summary>
    public class SourceRulesTests
    {

        #region NoTextOfThisVehiclePutsAnArticleBeforeAName()

        /// <summary>
        /// Nothing the vehicle says puts "a" or "an" in front of a name it
        /// interpolates. A session that named a certificate of another kind
        /// than its slot wants was refused as "'...' is a oemRoot and this
        /// names a contract", where it was set and again where a session
        /// started (found by the local controller, running the rule over the
        /// vehicle's sources).
        /// </summary>
        [Test]
        public void NoTextOfThisVehiclePutsAnArticleBeforeAName()
        {

            // Found by a file of each project, not by their directories: a
            // build with --artifacts-path has a directory named after each
            // project below artifacts/bin, where the rule read nothing and
            // passed (found by the charging station).
            var repository = SourceRules.RepositoryAbove(AppContext.BaseDirectory, "EV/EV.csproj",
                                                                                   "EVTests/EVTests.csproj");

            Assert.That(SourceRules.ArticlesBeforeANameIn(Path.Combine(repository, "EV")),
                        Is.Empty);

        }

        #endregion

    }

}
