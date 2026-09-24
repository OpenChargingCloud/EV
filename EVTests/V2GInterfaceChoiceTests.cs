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

using System.Net;

using NUnit.Framework;

using cloud.charging.open.EV.ISO15118;
using cloud.charging.open.protocols.ISO15118.NetworkInterfaces;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// Which interface a discovery uses when nobody said which.
    /// </summary>
    /// <remarks>
    /// Asked of a list rather than of this machine. The choice is the thing
    /// worth testing and it is a decision about a handful of values, so it is
    /// made where it can be handed a machine with two interfaces, then one,
    /// then none, without any of them existing.
    /// </remarks>
    public class V2GInterfaceChoiceTests
    {

        #region (private) Interface(Name, Index, HasIPv4)

        private static V2GNetworkInterface Interface(String   Name,
                                                     Int32    Index,
                                                     Boolean  HasIPv4)

            => new (Index,
                    Name,
                    IPAddress.Parse($"fe80::223:5ff:fe42:{Index:x}"),
                    [ 0x00, 0x23, 0x05, 0x42, 0x01, (Byte) Index ],
                    HasIPv4);

        #endregion


        #region TheOneWithoutIPv4Wins()

        /// <summary>
        /// The usual bench: one interface the machine is administered over and
        /// one with the charging station behind it.
        /// </summary>
        [Test]
        public void TheOneWithoutIPv4Wins()
        {

            var chosen = V2GLink.Choose([
                             Interface("enp0s4", 2, HasIPv4: true),
                             Interface("enp0s5", 3, HasIPv4: false)
                         ]);

            Assert.That(chosen?.Name,  Is.EqualTo("enp0s5"));

        }

        #endregion

        #region TheOneWithoutIPv4WinsWhereverItSits()

        /// <summary>
        /// And it wins because it has no IPv4, not because of where it sits in
        /// the list - which is what the old answer went by.
        /// </summary>
        [Test]
        public void TheOneWithoutIPv4WinsWhereverItSits()
        {

            var chosen = V2GLink.Choose([
                             Interface("enp0s5", 3, HasIPv4: false),
                             Interface("enp0s4", 2, HasIPv4: true)
                         ]);

            Assert.That(chosen?.Name,  Is.EqualTo("enp0s5"));

        }

        #endregion

        #region SeveralWithoutIPv4StillBeatTheOneWithIt()

        /// <summary>
        /// Two powerline modems beside one management interface: open between
        /// the modems, and settled against the management interface.
        /// </summary>
        /// <remarks>
        /// This is the case that decides what the rule is worth, and the one
        /// the other tests cannot see. Preferring "no IPv4" only when exactly
        /// one candidate qualifies would answer enp0s4 here - the single
        /// candidate that is certainly wrong - on the grounds that the two that
        /// might be right cannot be told apart. Being undecided between two
        /// plausible answers is not a reason to give an implausible one.
        ///
        /// Which of the two modems comes back is not asserted beyond its being
        /// one of them; that genuinely needs somebody to say, and --interface
        /// is how they say it.
        /// </remarks>
        [Test]
        public void SeveralWithoutIPv4StillBeatTheOneWithIt()
        {

            var chosen = V2GLink.Choose([
                             Interface("enp0s4", 2, HasIPv4: true),
                             Interface("plc0",   4, HasIPv4: false),
                             Interface("plc1",   5, HasIPv4: false)
                         ]);

            Assert.That(chosen?.Name,  Is.AnyOf("plc0", "plc1"),  "it answered with the one interface known to be wrong");

        }

        #endregion

        #region AllWithIPv4FallBackToTheFirst()

        /// <summary>
        /// A bench where something hands out IPv4 on the V2G segment too. The
        /// rule finds nothing to go by, which is not the same as there being
        /// nothing to use.
        /// </summary>
        [Test]
        public void AllWithIPv4FallBackToTheFirst()
        {

            var chosen = V2GLink.Choose([
                             Interface("enp0s4", 2, HasIPv4: true),
                             Interface("enp0s5", 3, HasIPv4: true)
                         ]);

            Assert.That(chosen?.Name,  Is.EqualTo("enp0s4"));

        }

        #endregion

        #region ASingleCandidateIsTakenEitherWay()

        [Test]
        public void ASingleCandidateIsTakenEitherWay()
        {

            Assert.Multiple(() =>
            {

                Assert.That(V2GLink.Choose([ Interface("enp0s5", 3, HasIPv4: false) ])?.Name,  Is.EqualTo("enp0s5"));

                // Even with an IPv4 address: one candidate is not a choice, and
                // refusing the only interface there is would help nobody.
                Assert.That(V2GLink.Choose([ Interface("enp0s4", 2, HasIPv4: true)  ])?.Name,  Is.EqualTo("enp0s4"));

            });

        }

        #endregion

        #region NoCandidatesIsNoAnswer()

        [Test]
        public void NoCandidatesIsNoAnswer()
        {

            Assert.That(V2GLink.Choose([]),  Is.Null);

        }

        #endregion

    }

}
