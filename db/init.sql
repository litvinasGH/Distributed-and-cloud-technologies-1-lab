-- Схема ЛР №4: фитнес-центр. Инициализируется PostgreSQL при первом создании volume.

CREATE TABLE IF NOT EXISTS trainers (
    id BIGSERIAL PRIMARY KEY,
    last_name VARCHAR(100) NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    middle_name VARCHAR(100) NOT NULL DEFAULT '',
    specialization VARCHAR(120) NOT NULL,
    experience_years INTEGER NOT NULL CHECK (experience_years >= 0),
    CONSTRAINT uq_trainers_name UNIQUE (last_name, first_name)
);

CREATE TABLE IF NOT EXISTS clients (
    id BIGSERIAL PRIMARY KEY,
    last_name VARCHAR(100) NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    middle_name VARCHAR(100) NOT NULL DEFAULT '',
    phone VARCHAR(40) NOT NULL UNIQUE,
    registered_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    current_membership_id BIGINT NULL
);

CREATE TABLE IF NOT EXISTS memberships (
    id BIGSERIAL PRIMARY KEY,
    client_id BIGINT NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
    name VARCHAR(120) NOT NULL,
    price NUMERIC(10, 2) NOT NULL CHECK (price >= 0),
    duration_days INTEGER NOT NULL CHECK (duration_days > 0),
    purchased_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_clients_current_membership') THEN
        ALTER TABLE clients ADD CONSTRAINT fk_clients_current_membership
            FOREIGN KEY (current_membership_id) REFERENCES memberships(id) ON DELETE SET NULL;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS training_slots (
    id BIGSERIAL PRIMARY KEY,
    trainer_id BIGINT NOT NULL REFERENCES trainers(id),
    training_time TIMESTAMP WITHOUT TIME ZONE NOT NULL,
    capacity INTEGER NOT NULL DEFAULT 1 CHECK (capacity > 0),
    available_places INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT chk_slot_places CHECK (available_places >= 0 AND available_places <= capacity),
    CONSTRAINT uq_trainer_training_time UNIQUE (trainer_id, training_time)
);

CREATE TABLE IF NOT EXISTS trainings (
    id BIGSERIAL PRIMARY KEY,
    client_id BIGINT NOT NULL REFERENCES clients(id),
    slot_id BIGINT NOT NULL REFERENCES training_slots(id),
    status VARCHAR(30) NOT NULL DEFAULT 'Запланирована'
        CHECK (status IN ('Запланирована', 'Завершена', 'Отменена')),
    booked_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_trainings_client_id ON trainings(client_id);
CREATE INDEX IF NOT EXISTS ix_trainings_slot_id ON trainings(slot_id);
CREATE INDEX IF NOT EXISTS ix_memberships_client_id ON memberships(client_id);

INSERT INTO trainers (last_name, first_name, middle_name, specialization, experience_years)
VALUES
    ('Иванов', 'Иван', 'Иванович', 'Фитнес', 5),
    ('Петрова', 'Анна', 'Сергеевна', 'Йога', 3)
ON CONFLICT (last_name, first_name) DO NOTHING;
