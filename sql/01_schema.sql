/* =====================================================================
   Trading demo database - schema
   Target: Azure SQL Database / SQL Server 2019+
   ALL DATA IS SYNTHETIC. Tickers/companies are invented; prices are simulated.
   Run as an admin (e.g. the Azure SQL server admin), NOT as the MCP login.
   ===================================================================== */

/* Safety check: never create objects in a system database. */
IF DB_NAME() IN (N'master', N'tempdb', N'model', N'msdb')
BEGIN
    RAISERROR(N'You are connected to a system database. Switch to the TradingDemo database first, then run this script again.', 16, 1);
    SET NOEXEC ON;   -- skip everything below
END
GO

CREATE SCHEMA trading;
GO

/* ---------- Reference data ---------------------------------------- */

CREATE TABLE trading.Instruments (
    InstrumentId  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Instruments PRIMARY KEY,
    Ticker        NVARCHAR(10)  NOT NULL CONSTRAINT UQ_Instruments_Ticker UNIQUE,
    Name          NVARCHAR(100) NOT NULL,
    Sector        NVARCHAR(50)  NOT NULL,
    Currency      CHAR(3)       NOT NULL
);

/* Daily OHLCV bars. Composite PK doubles as the "latest price" lookup index.
   (Column names avoid the reserved words OPEN / CLOSE.) */
CREATE TABLE trading.Prices (
    InstrumentId  INT           NOT NULL CONSTRAINT FK_Prices_Instruments REFERENCES trading.Instruments(InstrumentId),
    PriceDate     DATE          NOT NULL,
    OpenPrice     DECIMAL(18,4) NOT NULL,
    HighPrice     DECIMAL(18,4) NOT NULL,
    LowPrice      DECIMAL(18,4) NOT NULL,
    ClosePrice    DECIMAL(18,4) NOT NULL,
    Volume        BIGINT        NOT NULL,
    CONSTRAINT PK_Prices PRIMARY KEY (InstrumentId, PriceDate),
    CONSTRAINT CK_Prices_Range  CHECK (HighPrice >= LowPrice
                                   AND HighPrice >= OpenPrice AND HighPrice >= ClosePrice
                                   AND LowPrice  <= OpenPrice AND LowPrice  <= ClosePrice),
    CONSTRAINT CK_Prices_Positive CHECK (LowPrice > 0 AND Volume >= 0)
);

/* ---------- Clients & accounts ------------------------------------ */

CREATE TABLE trading.Clients (
    ClientId     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Clients PRIMARY KEY,
    FullName     NVARCHAR(100) NOT NULL,
    Email        NVARCHAR(200) MASKED WITH (FUNCTION = 'email()') NOT NULL,   -- masked for users without UNMASK
    Country      NVARCHAR(50)  NOT NULL,
    RiskProfile  NVARCHAR(20)  NOT NULL
        CONSTRAINT CK_Clients_Risk CHECK (RiskProfile IN ('Conservative','Balanced','Growth','Aggressive')),
    CreatedOn    DATE          NOT NULL
);

CREATE TABLE trading.Accounts (
    AccountId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Accounts PRIMARY KEY,
    ClientId       INT           NOT NULL CONSTRAINT FK_Accounts_Clients REFERENCES trading.Clients(ClientId),
    AccountNumber  NVARCHAR(20)  MASKED WITH (FUNCTION = 'partial(3,"XXXXX",3)') NOT NULL
        CONSTRAINT UQ_Accounts_Number UNIQUE,
    AccountType    NVARCHAR(20)  NOT NULL
        CONSTRAINT CK_Accounts_Type CHECK (AccountType IN ('Brokerage','Retirement','Joint')),
    BaseCurrency   CHAR(3)       NOT NULL,
    OpenedOn       DATE          NOT NULL
);
CREATE INDEX IX_Accounts_ClientId ON trading.Accounts(ClientId);

/* ---------- Trades -------------------------------------------------- */

CREATE TABLE trading.Trades (
    TradeId         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Trades PRIMARY KEY,
    AccountId       INT           NOT NULL CONSTRAINT FK_Trades_Accounts    REFERENCES trading.Accounts(AccountId),
    InstrumentId    INT           NOT NULL CONSTRAINT FK_Trades_Instruments REFERENCES trading.Instruments(InstrumentId),
    Side            CHAR(4)       NOT NULL CONSTRAINT CK_Trades_Side   CHECK (Side IN ('BUY','SELL')),
    Quantity        DECIMAL(18,4) NOT NULL CONSTRAINT CK_Trades_Qty    CHECK (Quantity > 0),
    Price           DECIMAL(18,4) NOT NULL CONSTRAINT CK_Trades_Price  CHECK (Price > 0),
    Fees            DECIMAL(18,2) NOT NULL CONSTRAINT DF_Trades_Fees DEFAULT (0),
    TradeTimestamp  DATETIME2(0)  NOT NULL,
    Status          NVARCHAR(10)  NOT NULL CONSTRAINT DF_Trades_Status DEFAULT ('SETTLED')
        CONSTRAINT CK_Trades_Status CHECK (Status IN ('PENDING','SETTLED','CANCELLED'))
);
-- Typical access paths: "trades for an account in a date range", "trades in an instrument"
CREATE INDEX IX_Trades_Account_Time ON trading.Trades(AccountId, TradeTimestamp DESC)
    INCLUDE (InstrumentId, Side, Quantity, Price, Fees, Status);
CREATE INDEX IX_Trades_Instrument_Time ON trading.Trades(InstrumentId, TradeTimestamp DESC);

/* ---------- Audit log (written by the MCP server) ------------------- */

CREATE TABLE trading.AuditLog (
    AuditId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    OccurredAtUtc  DATETIME2(3)  NOT NULL CONSTRAINT DF_AuditLog_Time DEFAULT (SYSUTCDATETIME()),
    Caller         NVARCHAR(200) NULL,        -- token subject / client id
    ToolName       NVARCHAR(100) NOT NULL,
    Parameters     NVARCHAR(MAX) NULL,        -- JSON of the arguments
    RowsReturned   INT           NULL,
    DurationMs     INT           NULL,
    Success        BIT           NOT NULL,
    Error          NVARCHAR(500) NULL
);
GO

/* ---------- Views: derived data, so nothing is stored twice ----------- */

/* Net holdings per account/instrument, derived from trades.
   AvgBuyPrice is deliberately simple (average of BUY prices). Real systems
   track tax lots / FIFO - a good "what I simplified" talking point. */
CREATE VIEW trading.v_Positions AS
SELECT  t.AccountId,
        t.InstrumentId,
        SUM(CASE WHEN t.Side = 'BUY' THEN t.Quantity ELSE -t.Quantity END) AS NetQuantity,
        SUM(CASE WHEN t.Side = 'BUY' THEN t.Quantity * t.Price ELSE 0 END)
          / NULLIF(SUM(CASE WHEN t.Side = 'BUY' THEN t.Quantity ELSE 0 END), 0) AS AvgBuyPrice
FROM    trading.Trades t
WHERE   t.Status <> 'CANCELLED'
GROUP BY t.AccountId, t.InstrumentId
HAVING  SUM(CASE WHEN t.Side = 'BUY' THEN t.Quantity ELSE -t.Quantity END) <> 0;
GO

/* Positions valued at the latest available close. */
CREATE VIEW trading.v_PositionValuation AS
SELECT  a.AccountId,
        a.AccountNumber,
        c.ClientId,
        c.FullName,
        i.Ticker,
        i.Name            AS InstrumentName,
        i.Sector,
        i.Currency,
        p.NetQuantity,
        p.AvgBuyPrice,
        lp.PriceDate      AS PriceAsOf,
        lp.ClosePrice     AS LastPrice,
        p.NetQuantity * lp.ClosePrice                    AS MarketValue,
        p.NetQuantity * (lp.ClosePrice - p.AvgBuyPrice)  AS UnrealisedPnL
FROM    trading.v_Positions p
JOIN    trading.Accounts    a ON a.AccountId    = p.AccountId
JOIN    trading.Clients     c ON c.ClientId     = a.ClientId
JOIN    trading.Instruments i ON i.InstrumentId = p.InstrumentId
OUTER APPLY (
        SELECT TOP (1) pr.PriceDate, pr.ClosePrice
        FROM   trading.Prices pr
        WHERE  pr.InstrumentId = p.InstrumentId
        ORDER BY pr.PriceDate DESC
) lp;
GO

SET NOEXEC OFF;   -- re-enable execution if the safety check above skipped the script
GO
