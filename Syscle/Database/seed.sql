-- Starter data for Web-Syscle. Runs after schema.sql; every statement is safe to repeat.
-- ============================ starter data ============================
-- Store rules (all editable in Admin > Settings; nothing below is hard-coded in the program)
INSERT IGNORE INTO Settings (SettingKey, SettingValue) VALUES
  ('ShowPaymentQr','0'), ('PaymentQrImage',''),
  ('StoreName','Web-Syscle'), ('StoreTagline','Shop & Customize'), ('DeliveryFee','100'),
  ('LowStockThreshold','5'), ('MaxFeatured','12'),
  ('AdminIdleSeconds','300'),
  ('UnpaidOnlineHours','2'), ('PickupHoldDays','2'), ('RefundWindowDays','7'),
  ('DailyExpenses','0'), ('DefaultMarginPct','30'),
  ('PromoPopup','1');

INSERT IGNORE INTO PaymentMethods (MethodCode, Label, IsOn) VALUES
  ('cash','Cash',1), ('gcash','GCash',1),
  ('paymongo','PayMongo',1), ('paypal','PayPal (online)',0);

-- Seasons (Philippine calendar). Edit the dates in Admin > Dropdown lists.
INSERT IGNORE INTO Seasons (Name, Kind, StartMD, EndMD) VALUES
  ('New Year',      'yearly','12-26','01-05'),
  ('Valentine''s',  'yearly','02-01','02-14'),
  ('Holy Week',     'easter','-7','0'),
  ('Summer',        'yearly','03-15','05-31'),
  ('Back to school','yearly','06-01','06-30'),
  ('Rainy season',  'yearly','07-01','09-30'),
  ('Anniversary',   'yearly','10-10','10-20'),
  ('Payday sale',   'monthly','29','2'),
  ('Christmas',     'yearly','12-01','12-25');

INSERT IGNORE INTO Lookups (Kind, Value, SortOrder) VALUES
  ('ItemType','Frame',1),('ItemType','Fork',2),('ItemType','Handlebar',3),('ItemType','Stem',4),('ItemType','Seatpost',5),('ItemType','Saddle',6),
  ('ItemType','Grips',7),('ItemType','Pedals',8),('ItemType','Crankset',9),('ItemType','Chain',10),('ItemType','Cassette',11),('ItemType','Derailleur',12),
  ('ItemType','Shifter',13),('ItemType','Brake',14),('ItemType','Brake pad',15),('ItemType','Rotor',16),('ItemType','Wheel',17),('ItemType','Rim',18),
  ('ItemType','Hub',19),('ItemType','Tire',20),('ItemType','Tube',21),('ItemType','Helmet',22),('ItemType','Gloves',23),('ItemType','Jersey',24),
  ('ItemType','Lights',25),('ItemType','Bell',26),('ItemType','Pump',27),('ItemType','Lock',28),('ItemType','Bottle & cage',29),('ItemType','Rack',30),
  ('ItemType','Fender',31),('ItemType','Tool',32),('ItemType','Spokes',33),('ItemType','Cockpit',34),('ItemType','Other',99);

INSERT IGNORE INTO Lookups (Kind, Value, Extra, SortOrder) VALUES
  ('SizeSet','Frame: S, M, L','S,M,L',1),
  ('SizeSet','Frame: 13-21 in','13,15,17,19,21',2),
  ('SizeSet','Wheel: 26, 27.5, 29','26,27.5,29',3),
  ('SizeSet','Wheel: 700c','700c',4),
  ('SizeSet','Tire: MTB sizes','26x1.95,27.5x2.1,29x2.2',5),
  ('SizeSet','Tire: road sizes','700x25c,700x28c,700x32c',6),
  ('SizeSet','Clothing: XS-XL','XS,S,M,L,XL',7);

INSERT IGNORE INTO Lookups (Kind, Value, SortOrder) VALUES
  ('Color','Black',1),('Color','White',2),('Color','Red',3),('Color','Blue',4),('Color','Green',5),('Color','Yellow',6),('Color','Orange',7),
  ('Color','Pink',8),('Color','Purple',9),('Color','Gray',10),('Color','Silver',11),('Color','Gold',12),('Color','Brown',13);

INSERT IGNORE INTO Lookups (Kind, Value, SortOrder) VALUES
  ('BikeType','Mountain bike',1),('BikeType','Road bike',2),('BikeType','Gravel bike',3),('BikeType','Fixie / Fixed gear',4),
  ('BikeType','Hybrid / City bike',5),('BikeType','BMX',6),('BikeType','Folding bike',7),('BikeType','Kids bike',8);

INSERT INTO Categories (Name)
SELECT t.n FROM (
  SELECT 'Mountain Bikes' AS n UNION ALL SELECT 'Road Bikes' UNION ALL SELECT 'Fixies / Fixed Gears'
  UNION ALL SELECT 'Gravel Bikes' UNION ALL SELECT 'Accessories' UNION ALL SELECT 'Others'
) t WHERE NOT EXISTS (SELECT 1 FROM Categories);

-- Starter parts (only inserted while the Products table is empty). Stock starts at 20.
INSERT INTO Products (CategoryId, ItemType, Name, Price, Stock, ImagePath, Sizes, Description, Specs, FitTags)
SELECT c.CategoryId, v.ItemType, v.Name, v.Price, v.Stock, v.ImagePath, v.Sizes, v.Description, v.Specs, v.FitTags
FROM (
  SELECT 'Mountain Bikes' AS CatName, 'Crankset' AS ItemType, 'Shimano Tourney Crank 3 Speed' AS Name, 250 AS Price, 20 AS Stock, '/Products/shimano-tourney-crank.jpg' AS ImagePath, CAST(NULL AS CHAR(200)) AS Sizes,
         'Entry-level 3-speed crankset for everyday riding.' AS Description, 'Speeds: 3\nChainrings: 42/34/24T' AS Specs, 'Mountain bike,Hybrid / City bike' AS FitTags
  UNION ALL SELECT 'Mountain Bikes', 'Rim', 'Sagmit Evo 3 Rims 26/27.5/29', 1200, 20, '/Products/sagmit-evo3-rims.jpg', '26,27.5,29', 'Light alloy rims, pick your wheel size.', 'Material: Alloy\nSizes: 26 / 27.5 / 29 in', 'Mountain bike'
  UNION ALL SELECT 'Mountain Bikes', 'Tire', 'Maxxis Pace 26/27.5/29', 1100, 20, '/Products/maxxis-pace.jpg', '26,27.5,29', 'Fast-rolling XC tire with low tread.', 'Use: Cross-country\nSizes: 26 / 27.5 / 29 in', 'Mountain bike'
  UNION ALL SELECT 'Road Bikes', 'Rim', 'Ragusa Rims 700c', 1000, 20, '/Products/ragusa-rims-700c.jpg', NULL, 'Road rims for 700c wheels.', 'Size: 700c', 'Road bike,Gravel bike'
  UNION ALL SELECT 'Road Bikes', 'Tire', 'Chaoyang Sharkskin Tires 700c x (23/25/28)', 650, 20, '/Products/chaoyang-sharkskin.jpg', '700x23,700x25,700x28', 'Slick road tire with a puncture-resistant belt.', 'Size: 700c x 23 / 25 / 28', 'Road bike'
  UNION ALL SELECT 'Accessories', 'Cockpit', 'Sagmit Evo 3 Handle Bar', 700, 20, '/Products/sagmit-handlebar.jpg', NULL, 'Alloy handlebar.', 'Clamp: 31.8 mm', 'Mountain bike,Hybrid / City bike'
  UNION ALL SELECT 'Accessories', 'Cockpit', 'Sagmit Evo 3 Seatpost 27.2/31.6', 650, 20, '/Products/sagmit-seatpost.jpg', '27.2,31.6', 'Alloy seatpost, choose the diameter of your frame.', 'Diameter: 27.2 / 31.6 mm', NULL
  UNION ALL SELECT 'Accessories', 'Cockpit', 'Sagmit Safari Saddle', 700, 20, '/Products/sagmit-saddle.jpg', NULL, 'Comfortable saddle for long rides.', 'Rails: Steel', NULL
  UNION ALL SELECT 'Accessories', 'Cockpit', 'Sagmit Systemone Stem', 650, 20, '/Products/sagmit-stem.jpg', NULL, 'Alloy stem.', 'Clamp: 31.8 mm', 'Mountain bike,Hybrid / City bike'
  UNION ALL SELECT 'Others', 'Derailleur', 'Shimano Tourney RD', 250, 20, '/Products/shimano-tourney-rd.jpg', NULL, 'Rear derailleur for 6/7/8-speed setups.', 'Speeds: 6 / 7 / 8', 'Mountain bike,Hybrid / City bike'
  UNION ALL SELECT 'Others', 'Derailleur', 'Shimano Tourney FD', 200, 20, '/Products/shimano-tourney-fd.jpg', NULL, 'Front derailleur.', 'Cranks: Triple', 'Mountain bike,Hybrid / City bike'
  UNION ALL SELECT 'Others', 'Tube', 'VeloStrim Innertubes 26/27.5/29/700c (23-45c)', 180, 20, '/Products/velostrim-innertubes.jpg', '26,27.5,29,700c', 'Butyl inner tube.', 'Valve: Presta', NULL
  UNION ALL SELECT 'Others', 'Spokes', 'Sagmit Spokes 26/27.5/29/700c', 450, 20, '/Products/sagmit-spokes.jpg', '26,27.5,29,700c', 'Stainless spokes sold per set.', 'Material: Stainless steel', NULL
) v JOIN Categories c ON c.Name = v.CatName
WHERE NOT EXISTS (SELECT 1 FROM Products);

UPDATE Products SET Sku = CONCAT('SY-', LPAD(ProductId, 5, '0')) WHERE Sku IS NULL OR Sku = '';

INSERT INTO PromoCodes (Code, DiscountPercent, IsPublic)
SELECT 'SYSCLE10', 10, 1 FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM PromoCodes);
