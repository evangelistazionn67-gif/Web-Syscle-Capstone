-- Adds size selection to a database that already exists. Run once; safe to run again.
-- (Carts are temporary, so the cart table is rebuilt and starts empty.)

SET @c := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Products' AND COLUMN_NAME = 'Sizes');
SET @s := IF(@c = 0, 'ALTER TABLE Products ADD COLUMN Sizes VARCHAR(200) NULL', 'SELECT 1');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;

SET @c := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'OrderItems' AND COLUMN_NAME = 'Size');
SET @s := IF(@c = 0, 'ALTER TABLE OrderItems ADD COLUMN Size VARCHAR(30) NOT NULL DEFAULT ""', 'SELECT 1');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;

SET @c := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'CartItems' AND COLUMN_NAME = 'Size');
SET @s := IF(@c = 0, 'DROP TABLE CartItems', 'SELECT 1');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;

CREATE TABLE IF NOT EXISTS CartItems (
  UserId    INT NOT NULL,
  ProductId INT NOT NULL,
  Size      VARCHAR(30) NOT NULL DEFAULT '',
  Qty       INT NOT NULL CHECK (Qty > 0),
  AddedAt   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (UserId, ProductId, Size),
  FOREIGN KEY (UserId)    REFERENCES Users(UserId)       ON DELETE CASCADE,
  FOREIGN KEY (ProductId) REFERENCES Products(ProductId) ON DELETE CASCADE
) ENGINE=InnoDB;

-- sizes for the parts that come in several sizes (only where none are set yet)
UPDATE Products SET Sizes = '26,27.5,29' WHERE Name LIKE 'Sagmit Evo 3 Rims%' AND Sizes IS NULL;
UPDATE Products SET Sizes = '26,27.5,29' WHERE Name LIKE 'Maxxis Pace%' AND Sizes IS NULL;
UPDATE Products SET Sizes = '700x23,700x25,700x28' WHERE Name LIKE 'Chaoyang Sharkskin%' AND Sizes IS NULL;
UPDATE Products SET Sizes = '27.2,31.6' WHERE Name LIKE 'Sagmit Evo 3 Seatpost%' AND Sizes IS NULL;
UPDATE Products SET Sizes = '26,27.5,29,700c' WHERE Name LIKE 'VeloStrim Innertubes%' AND Sizes IS NULL;
UPDATE Products SET Sizes = '26,27.5,29,700c' WHERE Name LIKE 'Sagmit Spokes%' AND Sizes IS NULL;

SELECT 'Size selection ready' AS Result;
