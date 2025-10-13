CREATE TABLE wallet.account (
    id VARCHAR(100) PRIMARY KEY,
	user_id VARCHAR(100) NOT NULL,
    balance NUMERIC(20,2) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
	updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);
CREATE INDEX idx_account_user_id
ON wallet.account (user_id);