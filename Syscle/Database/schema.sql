-- ============================================================================
-- Web-Syscle database schema (MySQL 8).  ONE file, always current.
--
--   * The website runs this file by itself when it starts (Seed.RunAsync), then adds any column an
--     older database is missing, then runs seed.sql. A new database therefore needs no manual SQL.
--     To run it by hand in MySQL Workbench: File > Open SQL Script > Execute, then run seed.sql. Safe to repeat.
--   * Databases created by an older version are upgraded by the same start-up code, so there are no
--     "Update_*.sql" files any more.
--   * Admin and demo accounts are created by the website, not by this file (the app hashes passwords).
-- ============================================================================

-- ---------- people ----------
-- One table for everyone. Role separates the Admin portal from the Customer shop.
CREATE TABLE IF NOT EXISTS Users (
  UserId            INT AUTO_INCREMENT PRIMARY KEY,
  Username          VARCHAR(50)  NOT NULL UNIQUE,
  Email             VARCHAR(120) NOT NULL UNIQUE,
  Phone             VARCHAR(30)  NULL,
  PasswordHash      VARCHAR(200) NOT NULL,
  Role              ENUM('SuperAdmin','Admin','Customer') NOT NULL,
  Status            ENUM('Active','Suspended') NOT NULL DEFAULT 'Active',
  FpRequest         TINYINT(1)   NOT NULL DEFAULT 0,
  TwoFactorEnabled  TINYINT(1)   NOT NULL DEFAULT 0,
  TwoFactorSecret   VARCHAR(200) NULL,             -- stored encrypted ("enc1:...")
  TwoFactorLastStep BIGINT       NULL,             -- last accepted 30-second code window (a code cannot be used twice)
  SessionVersion    INT          NOT NULL DEFAULT 0,
  LastSeen          DATETIME     NULL,
  LastActive        DATETIME     NULL,             -- last real click/keypress (used by the admin auto-logout)
  EmailNotify       TINYINT(1)   NOT NULL DEFAULT 1,
  CreatedAt         DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;

-- Failed sign-ins, kept in the database so a restart or a faked header cannot reset the count.
CREATE TABLE IF NOT EXISTS LoginAttempts (
  AttemptId  INT AUTO_INCREMENT PRIMARY KEY,
  AttemptKey VARCHAR(120) NOT NULL,                -- "acc|username" or "ip|address"
  AtTime     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX IX_LoginAttempts_Key (AttemptKey, AtTime)
) ENGINE=InnoDB;

-- One-time tickets for the 2FA step and for SMS password reset. Only a hash of the ticket (and of the SMS code) is stored.
CREATE TABLE IF NOT EXISTS AuthTickets (
  TicketHash CHAR(64)    PRIMARY KEY,
  UserId     INT         NOT NULL,
  Purpose    VARCHAR(10) NOT NULL,                 -- '2fa' or 'sms'
  CodeHash   CHAR(64)    NULL,
  Attempts   INT         NOT NULL DEFAULT 0,
  ExpiresAt  DATETIME    NOT NULL,
  INDEX IX_AuthTickets_User (UserId, Purpose)
) ENGINE=InnoDB;

-- Keys that sign the login cookies and reset links (kept here instead of in text files on the disk).
CREATE TABLE IF NOT EXISTS DataProtectionKeys (
  KeyId        INT AUTO_INCREMENT PRIMARY KEY,
  FriendlyName VARCHAR(200) NOT NULL,
  Xml          TEXT         NOT NULL
) ENGINE=InnoDB;

-- Who did what in the admin portal.
CREATE TABLE IF NOT EXISTS AuditLog (
  AuditId   INT AUTO_INCREMENT PRIMARY KEY,
  ByUser    VARCHAR(50)  NULL,
  Action    VARCHAR(40)  NOT NULL,
  Detail    VARCHAR(300) NULL,
  CreatedAt DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX IX_Audit_Date (CreatedAt)
) ENGINE=InnoDB;

-- ---------- catalog ----------
-- Bike-type tiles on the storefront (Mountain Bikes, Road Bikes, ...), with a picture each.
CREATE TABLE IF NOT EXISTS Categories (
  CategoryId INT AUTO_INCREMENT PRIMARY KEY,
  Name       VARCHAR(60)  NOT NULL UNIQUE,
  ImagePath  VARCHAR(260) NULL
) ENGINE=InnoDB;

-- Parts. ItemType, sizes, colors and "fits" come from the Lookups table (no lists are typed into the code).
CREATE TABLE IF NOT EXISTS Products (
  ProductId   INT AUTO_INCREMENT PRIMARY KEY,
  CategoryId  INT NOT NULL,
  ItemType    VARCHAR(60)    NOT NULL,
  Name        VARCHAR(100)   NOT NULL,
  Price       DECIMAL(10,2)  NOT NULL CHECK (Price >= 0),
  CostPrice   DECIMAL(10,2)  NULL,                 -- what the shop paid (optional; used for the profit estimate)
  Stock       INT            NOT NULL CHECK (Stock >= 0),
  ImagePath   VARCHAR(260)   NULL,
  Sizes       VARCHAR(200)   NULL,                 -- comma list such as 26,27.5,29 (customers must pick one)
  Description TEXT           NULL,                 -- what the part is and what it is for
  Specs       TEXT           NULL,                 -- one "Label: value" per line (weight, material, hub width, ...)
  FitTags     VARCHAR(250)   NULL,                 -- comma list of bike types it fits (empty = universal)
  Sku         VARCHAR(30)    NULL,                 -- printed on the QR label; scanning it adds the item to a walk-in sale
  IsFeatured  TINYINT(1)     NOT NULL DEFAULT 0,
  AddedAt     DATETIME       NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY UX_Products_Sku (Sku),
  FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS ProductVariants (
  VariantId  INT AUTO_INCREMENT PRIMARY KEY,
  ProductId  INT NOT NULL,
  ColorName  VARCHAR(50)  NOT NULL,
  ImagePath  VARCHAR(260) NULL,
  FOREIGN KEY (ProductId) REFERENCES Products(ProductId) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Editable dropdown lists: Kind = ItemType | SizeSet | Color | BikeType. (SizeSet: Value = label, Extra = the comma list of sizes.)
CREATE TABLE IF NOT EXISTS Lookups (
  LookupId  INT AUTO_INCREMENT PRIMARY KEY,
  Kind      VARCHAR(20)  NOT NULL,
  Value     VARCHAR(80)  NOT NULL,
  Extra     VARCHAR(200) NULL,
  SortOrder INT          NOT NULL DEFAULT 0,
  UNIQUE KEY UX_Lookups (Kind, Value)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS Reviews (
  ReviewId  INT AUTO_INCREMENT PRIMARY KEY,
  ProductId INT NOT NULL,
  UserId    INT NOT NULL,
  Rating    INT NOT NULL CHECK (Rating BETWEEN 1 AND 5),
  Comment   VARCHAR(1000) NULL,
  CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (ProductId) REFERENCES Products(ProductId) ON DELETE CASCADE,
  FOREIGN KEY (UserId)    REFERENCES Users(UserId)       ON DELETE CASCADE
) ENGINE=InnoDB;

-- ---------- cart ----------
CREATE TABLE IF NOT EXISTS CartItems (
  UserId    INT NOT NULL,
  ProductId INT NOT NULL,
  Size      VARCHAR(30) NOT NULL DEFAULT '',
  Color     VARCHAR(50) NOT NULL DEFAULT '',
  Qty       INT NOT NULL CHECK (Qty > 0),
  AddedAt   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (UserId, ProductId, Size, Color),
  FOREIGN KEY (UserId)    REFERENCES Users(UserId)       ON DELETE CASCADE,
  FOREIGN KEY (ProductId) REFERENCES Products(ProductId) ON DELETE CASCADE
) ENGINE=InnoDB;

-- ---------- promotions ----------
-- MaxUses NULL = unlimited. Season NULL = not seasonal. IsPublic = announce it in the pop-up and banner.
CREATE TABLE IF NOT EXISTS PromoCodes (
  Code            VARCHAR(30) PRIMARY KEY,
  DiscountPercent INT NOT NULL CHECK (DiscountPercent BETWEEN 1 AND 90),
  IsActive        TINYINT(1) NOT NULL DEFAULT 1,
  StartsAt        DATE NULL,
  EndsAt          DATE NULL,
  MaxUses         INT NULL,
  MaxPerUser      INT NULL,          -- times ONE customer may use the code (NULL = no limit per customer)
  Season          VARCHAR(30) NULL,
  IsPublic        TINYINT(1) NOT NULL DEFAULT 0
) ENGINE=InnoDB;

-- Seasons switch seasonal codes and bundles on and off by themselves every year.
-- Kind: yearly (StartMD/EndMD = "MM-DD"), easter (day offsets from Easter Sunday), monthly (StartMD/EndMD = day of the month; start > end wraps into the next month).
CREATE TABLE IF NOT EXISTS Seasons (
  Name    VARCHAR(30) PRIMARY KEY,
  Kind    VARCHAR(10) NOT NULL DEFAULT 'yearly',
  StartMD VARCHAR(5)  NOT NULL,
  EndMD   VARCHAR(5)  NOT NULL,
  IsOn    TINYINT(1)  NOT NULL DEFAULT 1
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS Bundles (
  BundleId        INT AUTO_INCREMENT PRIMARY KEY,
  Name            VARCHAR(160) NOT NULL,
  Description     VARCHAR(500) NULL,
  ImagePath       VARCHAR(260) NULL,
  DiscountPercent INT NOT NULL CHECK (DiscountPercent BETWEEN 1 AND 100),
  IsActive        TINYINT(1) NOT NULL DEFAULT 1,
  AutoMade        TINYINT(1) NOT NULL DEFAULT 0,
  Kind            VARCHAR(10) NOT NULL DEFAULT 'Bundle',
  Season          VARCHAR(30) NULL,
  StartsAt        DATE NULL,
  EndsAt          DATE NULL,
  CreatedAt       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS BundleItems (
  BundleId  INT NOT NULL,
  ProductId INT NOT NULL,
  Qty       INT NOT NULL DEFAULT 1 CHECK (Qty > 0),
  PRIMARY KEY (BundleId, ProductId),
  FOREIGN KEY (BundleId)  REFERENCES Bundles(BundleId)   ON DELETE CASCADE,
  FOREIGN KEY (ProductId) REFERENCES Products(ProductId) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS CartBundles (
  UserId   INT NOT NULL,
  BundleId INT NOT NULL,
  Qty      INT NOT NULL CHECK (Qty > 0),
  PRIMARY KEY (UserId, BundleId),
  FOREIGN KEY (UserId)   REFERENCES Users(UserId)     ON DELETE CASCADE,
  FOREIGN KEY (BundleId) REFERENCES Bundles(BundleId) ON DELETE CASCADE
) ENGINE=InnoDB;

-- ---------- orders ----------
CREATE TABLE IF NOT EXISTS PaymentMethods (
  MethodCode    VARCHAR(20) PRIMARY KEY,
  Label         VARCHAR(60) NOT NULL,
  IsOn          TINYINT(1)  NOT NULL DEFAULT 1,
  AccountNumber VARCHAR(60) NULL,
  AccountName   VARCHAR(80) NULL
) ENGINE=InnoDB;

-- Online orders and walk-in sales. The order number (ORD-000001 / WLK-000001) is built from OrderId + Channel.
-- Money rule: a sale only counts in the reports when the items were confirmed AND the payment was received (see Sql.Rev in Program.cs).
CREATE TABLE IF NOT EXISTS Orders (
  OrderId            INT AUTO_INCREMENT PRIMARY KEY,
  UserId             INT NULL,
  CustomerName       VARCHAR(80)   NOT NULL,
  Channel            ENUM('Online','Walk-in') NOT NULL,
  PaymentMethod      VARCHAR(60)   NOT NULL,
  PaymentRef         VARCHAR(60)   NULL,           -- cs_... (PayMongo) / pp_... (PayPal); orders from before PayMongo-only may hold a GCash reference
  PaymentStatus      VARCHAR(10)   NOT NULL DEFAULT 'Unpaid',
  PaidAt             DATETIME      NULL,
  PaymentConfirmedBy VARCHAR(50)   NULL,
  PromoCode          VARCHAR(30)   NULL,
  Discount           DECIMAL(10,2) NOT NULL DEFAULT 0,
  BundleDiscount     DECIMAL(10,2) NOT NULL DEFAULT 0,
  Total              DECIMAL(10,2) NOT NULL,
  Status             ENUM('Pending','Preparing','Ready','Out for Delivery','Completed','Cancelled') NOT NULL,
  CancelReason       VARCHAR(120)  NULL,
  ItemsConfirmedAt   DATETIME      NULL,           -- the store checked the items first ...
  ItemsConfirmedBy   VARCHAR(50)   NULL,
  FulfillmentType    VARCHAR(10)   NOT NULL DEFAULT 'Pickup',
  PickupDate         DATE NULL,
  DeliveryAddress    VARCHAR(250)  NULL,
  DeliveryPhone      VARCHAR(20)   NULL,
  DeliveryFee        DECIMAL(10,2) NOT NULL DEFAULT 0,
  Courier            VARCHAR(60)   NULL,
  TrackingNo         VARCHAR(60)   NULL,
  ReadyAt            DATETIME      NULL,
  CompletedAt        DATETIME      NULL,
  RefundStatus       VARCHAR(12)   NOT NULL DEFAULT 'None',   -- None | Requested | Rejected | Refunded
  RefundReason       VARCHAR(300)  NULL,
  RefundAmount       DECIMAL(10,2) NULL,
  RefundRequestedAt  DATETIME      NULL,
  RefundedAt         DATETIME      NULL,
  RefundNote         VARCHAR(300)  NULL,
  CreatedAt          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX IX_Orders_Status (Status, CreatedAt),
  FOREIGN KEY (UserId) REFERENCES Users(UserId) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS OrderItems (
  OrderItemId INT AUTO_INCREMENT PRIMARY KEY,
  OrderId     INT NOT NULL,
  ProductId   INT NULL,
  ProductName VARCHAR(100)  NOT NULL,   -- snapshot, so history survives product edits and deletes
  Size        VARCHAR(30)   NOT NULL DEFAULT '',
  Color       VARCHAR(50)   NOT NULL DEFAULT '',
  UnitPrice   DECIMAL(10,2) NOT NULL,
  UnitCost    DECIMAL(10,2) NULL,       -- snapshot of the cost price at the time of the sale
  Qty         INT NOT NULL CHECK (Qty > 0),
  FOREIGN KEY (OrderId)   REFERENCES Orders(OrderId)     ON DELETE CASCADE,
  FOREIGN KEY (ProductId) REFERENCES Products(ProductId) ON DELETE SET NULL
) ENGINE=InnoDB;

-- Every change to a product's stock (sale, cancel, restock, refund, edit).
CREATE TABLE IF NOT EXISTS StockMovements (
  MovementId  INT AUTO_INCREMENT PRIMARY KEY,
  ProductId   INT NULL,
  ProductName VARCHAR(120) NOT NULL,
  Delta       INT NOT NULL,
  StockAfter  INT NOT NULL,
  Reason      VARCHAR(40) NOT NULL,
  Ref         VARCHAR(30) NULL,
  ByUser      VARCHAR(50) NULL,
  CreatedAt   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX IX_Stock_Product (ProductId),
  INDEX IX_Stock_Date (CreatedAt)
) ENGINE=InnoDB;

-- ---------- chat and settings ----------
CREATE TABLE IF NOT EXISTS Messages (
  MessageId    INT AUTO_INCREMENT PRIMARY KEY,
  ThreadUserId INT NOT NULL,
  Sender       ENUM('Customer','Staff') NOT NULL,
  Body         VARCHAR(500) NOT NULL,
  IsRead       TINYINT(1) NOT NULL DEFAULT 0,
  CreatedAt    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX IX_Messages_Thread (ThreadUserId, MessageId),
  FOREIGN KEY (ThreadUserId) REFERENCES Users(UserId) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS Settings (
  SettingKey   VARCHAR(60) PRIMARY KEY,
  SettingValue VARCHAR(260) NULL
) ENGINE=InnoDB;

-- Money the store takes out of its PayMongo wallet to a GCash account. Every withdrawal needs the admin's 2FA code; the rows below are the audit trail.
CREATE TABLE IF NOT EXISTS WithdrawalAccounts (
  AccountId     INT AUTO_INCREMENT PRIMARY KEY,
  AccountName   VARCHAR(80) NOT NULL,          -- the name registered on the GCash account
  AccountNumber VARCHAR(15) NOT NULL,          -- the GCash mobile number, 09XXXXXXXXX
  CreatedBy     VARCHAR(50) NULL,
  CreatedAt     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_wacct_number (AccountNumber)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS Withdrawals (
  WithdrawalId  INT AUTO_INCREMENT PRIMARY KEY,
  Reference     VARCHAR(20) NOT NULL DEFAULT '',   -- WD-000001
  AccountName   VARCHAR(80) NOT NULL,
  AccountNumber VARCHAR(15) NOT NULL,
  Amount        DECIMAL(12,2) NOT NULL,
  Status        VARCHAR(12) NOT NULL DEFAULT 'Pending',   -- Pending, Succeeded, Failed
  TransferId    VARCHAR(60) NULL,                  -- PayMongo's id for the transfer
  Error         VARCHAR(300) NULL,
  RequestedBy   VARCHAR(50) NULL,
  RequestedAt   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UpdatedAt     DATETIME NULL,
  KEY ix_wd_status (Status)
) ENGINE=InnoDB;
