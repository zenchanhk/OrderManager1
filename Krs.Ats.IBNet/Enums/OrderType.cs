using System;
using System.ComponentModel;

namespace Krs.Ats.IBNet
{
    /// <summary>
    /// Order Type Enumerations
    /// </summary>
    [Serializable()] 
    public enum OrderType
    {
        /// <summary>
        /// A Market order is an order to buy or sell an asset at the bid or offer price currently available in the marketplace.
        /// Bonds, Forex, Futures, Future Options, Options, Stocks, Warrants
        /// </summary>
        [Description("MKT")] Market,
        /// <summary>
        /// A market order that is submitted to execute as close to the closing price as possible.
        /// Non US Futures, Non US Options, Stocks
        /// </summary>
        //Changed from MKTCLS to MOC based on input from TWS
        [Description("MOC")] MarketOnClose,
        /// <summary>
        /// A limit order is an order to buy or sell a contract at a specified price or better.
        /// Bonds, Forex, Futures, Future Options, Options, Stocks, Warrants
        /// </summary>
        [Description("LMT")] Limit,
        /// <summary>
        /// An LOC (Limit-on-Close) order that executes at the closing price if the closing price is at or better than the submitted limit price, according to the rules of the specific exchange. Otherwise the order will be cancelled. 
        /// Non US Futures , Stocks
        /// </summary>
        [Description("LOC")] LimitOnClose,
        /// <summary>
        /// An order that is pegged to buy on the best offer and sell on the best bid.
        /// Your order is pegged to buy on the best offer and sell on the best bid. You can also use an offset amount which is subtracted from the best offer for a buy order, and added to the best bid for a sell order.
        /// Stocks
        /// </summary>
        [Description("PEGMKT")] PeggedToMarket,
        /// <summary>
        /// A Stop order becomes a market order to buy or sell securities or commodities once the specified stop price is attained or penetrated.
        /// Forex, Futures, Future Options, Options, Stocks, Warrants
        /// </summary>
        [Description("STP")] Stop,
        /// <summary>
        /// A STOP-LIMIT order is similar to a stop order in that a stop price will activate the order. However, once activated, the stop-limit order becomes a buy limit or sell limit order and can only be executed at a specific price or better. It is a combination of both the stop order and the limit order.
        /// Forex, Futures, Options, Stocks
        /// </summary>
        [Description("STP LMT")] StopLimit,
        /// <summary>
        /// A trailing stop for a sell order sets the stop price at a fixed amount below the market price. If the market price rises, the stop loss price rises by the increased amount, but if the stock price falls, the stop loss price remains the same. The reverse is true for a buy trailing stop order.
        /// Forex, Futures, Future Options, Options, Stocks, Warrants
        /// </summary>
        [Description("TRAIL")] TrailingStop,
        /// <summary>
        /// A Relative order derives its price from a combination of the market quote and a user-defined offset amount. The order is submitted as a limit order and modified according to the pricing logic until it is executed or you cancel the order.
        /// Options, Stocks
        /// </summary>
        [Description("REL")] Relative,
        /// <summary>
        /// The VWAP for a stock is calculated by adding the dollars traded for every transaction in that stock ("price" x "number of shares traded") and dividing the total shares traded. By default, a VWAP order is computed from the open of the market to the market close, and is calculated by volume weighting all transactions during this time period. TWS allows you to modify the cut-off and expiration times using the Time in Force and Expiration Date fields, respectively.
        /// Stocks
        /// </summary>
        [Description("VWAP")] VolumeWeightedAveragePrice,
        /// <summary>
        /// A trailing stop limit for a sell order sets the stop price at a fixed amount below the market price and defines a limit price for the sell order. If the market price rises, the stop loss price rises by the increased amount, but if the stock price falls, the stop loss price remains the same. When the order triggers, a limit order is submitted at the price you defined. The reverse is true for a buy trailing stop limit order.
        /// Forex, Futures, Future Options, Options, Stocks, Warrants
        /// </summary>
        [Description("TRAILLIMIT")] TrailingStopLimit,
        /// <summary>
        /// TWS Version 857 introduced volatility trading of options, and a new order type, "VOL." What happens with VOL orders is that the limit price that is sent to the exchange is computed by TWS as a function of a daily or annualized option volatility provided by the user. VOL orders can be placed for any US option that trades on the BOX exchange. VOL orders are eligible for dynamic management, a powerful new functionality wherein TWS can manage options orders in response to specifications set by the user.
        /// </summary>
        [Description("VOL")] Volatility,
        /// <summary>
        /// VOL orders only. Enter an order type to instruct TWS to submit a
        /// delta neutral trade on full or partial execution of the VOL order.
        /// For no hedge delta order to be sent, specify NONE.
        /// </summary>
        [Description("NONE")] None,
        /// <summary>
        /// Used to initialize the delta Order Field.
        /// </summary>
        [Description("")] Empty,
        /// <summary>
        /// Default - used for Delta Neutral Order Type
        /// </summary>
        [Description("Default")] Default,
        /// <summary>
        /// Scale Order.
        /// </summary>
        [Description("SCALE")] Scale,
        /// <summary>
        /// Market if Touched Order.
        /// </summary>
        [Description("MIT")] MarketIfTouched,
        /// <summary>
        /// Market with Protection Order.
        /// </summary>
        [Description("MKT PRT")] MarketWithProtection,
        /// <summary>
        /// Stop with Protection Order.
        /// </summary>
        [Description("STP PRT")] StopWithProtection,
        /// <summary>
        /// Auction Order.
        /// </summary>
        [Description("MTL")] Auction,
        /// <summary>
        /// Limit if Touched Order.
        /// </summary>
        [Description("LIT")] LimitIfTouched
    }
}