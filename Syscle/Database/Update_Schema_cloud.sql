-- Brings an EXISTING database up to what Program.cs expects (SuperAdmin role, 2FA, colours, reviews, settings,
-- bundle discount range). Run once; safe to run again. New installs do not need it (Syscle_MySQL.sql already has all of this).

ALTER TABLE Users MODIFY Role ENUM('SuperAdmin','Admin','Customer') NOT NULL;
SET @c := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Users' AND COLUMN_NAME = 'TwoFactorEnabled');
SET @s := IF(@c = 0, 'ALTER TABLE Users ADD COLUMN TwoFactorEnabled TINYINT(1) NOT NULL DEFAULT 0', 'SELECT 1');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
SET @c := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Users' AND COLUMN_NAME = 'TwoFactorSecret');
SET @s := IF(@c = 0, 'ALTER TABLE Users ADD COLUMN TwoFactorSecret VARCHAR(64) NULL', 'SELECT 1');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
SET @c := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'CartItems' AND COLUMN_NAME = 'Color');
SET @s := IF(@c = 0, 'ALTER TABLE CartItems ADD COLUMN Color VARCHAR(50) NOT NULL DEFAULT ""', 'SELECT 1');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
SET @c := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'OrderItems' AND COLUMN_NAME = 'Color');
SET @s := IF(@c = 0, 'ALTER TABLE OrderItems ADD COLUMN Color VARCHAR(50) NOT NULL DEFAULT ""', 'SELECT 1');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;

-- cart primary key must include Color, otherwise the same part in two colours collides
SET @pk := (SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'CartItems' AND CONSTRAINT_NAME = 'PRIMARY');
SET @s := IF(@pk = 'UserId,ProductId,Size,Color', 'SELECT 1', 'ALTER TABLE CartItems DROP PRIMARY KEY, ADD PRIMARY KEY (UserId, ProductId, Size, Color)');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;

-- admins may create bundles with any 1-100% discount, so the old 5-10 CHECK must go
SET @ck := (SELECT GROUP_CONCAT(CONSTRAINT_NAME) FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND CHECK_CLAUSE LIKE '%DiscountPercent%' AND CONSTRAINT_NAME IN (SELECT CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Bundles'));
SET @s := IF(@ck IS NULL, 'SELECT 1', CONCAT('ALTER TABLE Bundles DROP CHECK ', @ck));
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
SET @s := IF(@ck IS NULL, 'SELECT 1', 'ALTER TABLE Bundles ADD CONSTRAINT CK_Bundles_Discount CHECK (DiscountPercent BETWEEN 1 AND 100)');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;

CREATE TABLE IF NOT EXISTS ProductVariants (
  VariantId  INT AUTO_INCREMENT PRIMARY KEY,
  ProductId  INT NOT NULL,
  ColorName  VARCHAR(50)  NOT NULL,
  ImagePath  VARCHAR(260) NULL,
  FOREIGN KEY (ProductId) REFERENCES Products(ProductId) ON DELETE CASCADE
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

CREATE TABLE IF NOT EXISTS Settings (
  SettingKey   VARCHAR(60) PRIMARY KEY,
  SettingValue VARCHAR(260) NULL
) ENGINE=InnoDB;
INSERT IGNORE INTO Settings (SettingKey, SettingValue) VALUES ('ShowPaymentQr','0'), ('PaymentQrImage', '');

-- the seeded 'admin' account is meant to be SuperAdmin
UPDATE Users SET Role = 'SuperAdmin' WHERE Username = 'admin' AND Role = 'Admin' AND NOT EXISTS (SELECT 1 FROM (SELECT 1 FROM Users WHERE Role = 'SuperAdmin') x);

-- ---- v3: GCash reference numbers, e-mail preference, customer messages (the app also adds these by itself on start) ----
SET @c := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Orders' AND COLUMN_NAME = 'PaymentRef');
SET @s := IF(@c = 0, 'ALTER TABLE Orders ADD COLUMN PaymentRef VARCHAR(40) NULL', 'SELECT 1');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
SET @c := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Users' AND COLUMN_NAME = 'EmailNotify');
SET @s := IF(@c = 0, 'ALTER TABLE Users ADD COLUMN EmailNotify TINYINT(1) NOT NULL DEFAULT 1', 'SELECT 1');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
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

SELECT 'Schema is up to date' AS Result;
