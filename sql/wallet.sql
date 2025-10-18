CREATE TABLE wallet.account (
    id VARCHAR(100) PRIMARY KEY,
	user_id VARCHAR(100) NOT NULL,
    balance NUMERIC(20,2) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_account_user_id
ON wallet.account (user_id);

CREATE TABLE wallet.inbox (
    transaction_id TEXT PRIMARY KEY,
    type VARCHAR(100) NOT NULL,
    amount NUMERIC(20,2) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);