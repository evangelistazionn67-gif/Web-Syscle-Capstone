USE WebSyscleDB;
-- EMERGENCY: gives accounts a known password again (for example after a failed password-reset test).
-- Run only the line(s) you need, then log in and change the password in My Account.
--   admin      -> admin123
--   customer1  -> password123
-- (Any account can be fixed the same way: change the WHERE part.)

UPDATE Users SET PasswordHash = 'v1.100000.vNzd8Z/mPBbnx12aQQY98Q==.pZSn1g21qSHoWXwn1hE/H8I0WxzbzI7CydmxRmSwuUk=', SessionVersion = SessionVersion + 1 WHERE Username = 'admin';
UPDATE Users SET PasswordHash = 'v1.100000.7T7IMsJm4HD6UYqsXWA/8w==.jPItVGFtmDjViKe+4S4qXn0xi2d9sN9glIrsA+FjeLA=', SessionVersion = SessionVersion + 1 WHERE Username = 'customer1';

-- Also switch OFF two-factor for an account you cannot get into (lost phone):
-- UPDATE Users SET TwoFactorEnabled = 0, TwoFactorSecret = NULL WHERE Username = 'admin';

SELECT UserId, Username, Email, Role, TwoFactorEnabled FROM Users;
