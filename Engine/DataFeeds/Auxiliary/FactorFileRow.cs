﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace QuantConnect.Lean.Engine.DataFeeds.Auxiliary
{
    /// <summary>
    /// Defines a single row in a factor_factor file. This is a csv file ordered as {date, price factor, split factor}
    /// </summary>
    public class FactorFileRow
    {
        /// <summary>
        /// Gets the date associated with this data
        /// </summary>
        public DateTime Date { get; private set; }

        /// <summary>
        /// Gets the price factor associated with this data
        /// </summary>
        public decimal PriceFactor { get; private set; }

        /// <summary>
        /// Gets te split factored associated with the date
        /// </summary>
        public decimal SplitFactor { get; private set; }

        /// <summary>
        /// Gets the combined factor used to create adjusted prices from raw prices
        /// </summary>
        public decimal PriceScaleFactor
        {
            get { return PriceFactor*SplitFactor; }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FactorFileRow"/> class
        /// </summary>
        public FactorFileRow(DateTime date, decimal priceFactor, decimal splitFactor)
        {
            Date = date;
            PriceFactor = priceFactor;
            SplitFactor = splitFactor;
        }

        /// <summary>
        /// Reads in the factor file for the specified equity symbol
        /// </summary>
        public static IEnumerable<FactorFileRow> Read(string symbol, string market)
        {
            string path = Path.Combine(Constants.DataFolder, "equity", market, "factor_files", symbol.ToLower() + ".csv");
            foreach (var line in File.ReadAllLines(path))
            {
                var csv = line.Split(',');
                yield return new FactorFileRow(
                    DateTime.ParseExact(csv[0], DateFormat.EightCharacter, CultureInfo.InvariantCulture, DateTimeStyles.None),
                    decimal.Parse(csv[1], CultureInfo.InvariantCulture),
                    decimal.Parse(csv[2], CultureInfo.InvariantCulture)
                    );
            }
        }
    }
}