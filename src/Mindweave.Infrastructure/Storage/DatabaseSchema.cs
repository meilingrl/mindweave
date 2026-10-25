namespace Mindweave.Infrastructure.Storage;

public static class DatabaseSchema
{
    public static IReadOnlyList<SchemaMigration> Migrations { get; } = Array.AsReadOnly<SchemaMigration>([new(1, """
        CREATE TABLE workspace (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL CHECK(length(trim(name)) BETWEEN 1 AND 160),
            created_at TEXT NOT NULL
        );
        CREATE TABLE app_selection (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            workspace_id TEXT NOT NULL REFERENCES workspace(id) ON DELETE CASCADE,
            area TEXT NOT NULL CHECK(area IN ('Explore','Network','Settings'))
        );
        """), new(2, """
        CREATE TABLE conversation (
            id TEXT PRIMARY KEY NOT NULL,
            workspace_id TEXT NOT NULL REFERENCES workspace(id) ON DELETE CASCADE,
            title TEXT NOT NULL CHECK(length(trim(title)) BETWEEN 1 AND 160),
            title_is_manual INTEGER NOT NULL DEFAULT 0 CHECK(title_is_manual IN (0,1)),
            title_revision INTEGER NOT NULL DEFAULT 0 CHECK(title_revision>=0),
            created_at TEXT NOT NULL
        );
        CREATE INDEX conversation_workspace ON conversation(workspace_id,created_at,id);
        CREATE TABLE conversation_draft (
            conversation_id TEXT PRIMARY KEY NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
            text TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK(revision>=0)
        );
        CREATE TABLE chat_message (
            id TEXT PRIMARY KEY NOT NULL,
            conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
            sequence INTEGER NOT NULL CHECK(sequence>0),
            role TEXT NOT NULL CHECK(role IN ('User','Assistant')),
            body TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('Queued','Running','Completed','Stopped','Failed','Interrupted')),
            revision INTEGER NOT NULL CHECK(revision>=0),
            reply_to_user_id TEXT,
            supersedes_message_id TEXT,
            UNIQUE(id,conversation_id),
            UNIQUE(conversation_id,sequence),
            FOREIGN KEY(reply_to_user_id,conversation_id) REFERENCES chat_message(id,conversation_id),
            FOREIGN KEY(supersedes_message_id,conversation_id) REFERENCES chat_message(id,conversation_id),
            CHECK((role='User' AND reply_to_user_id IS NULL AND supersedes_message_id IS NULL AND status='Completed')
                OR (role='Assistant' AND reply_to_user_id IS NOT NULL))
        );
        CREATE UNIQUE INDEX one_active_answer ON chat_message(conversation_id) WHERE status IN ('Queued','Running');
        CREATE TRIGGER message_source_role BEFORE INSERT ON chat_message WHEN NEW.role='Assistant'
        BEGIN
            SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM chat_message WHERE id=NEW.reply_to_user_id
                AND conversation_id=NEW.conversation_id AND role='User' AND sequence<NEW.sequence)
                THEN RAISE(ABORT,'Invalid reply source') END;
            SELECT CASE WHEN NEW.supersedes_message_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM chat_message
                WHERE id=NEW.supersedes_message_id AND conversation_id=NEW.conversation_id
                AND role='Assistant' AND reply_to_user_id=NEW.reply_to_user_id AND sequence<NEW.sequence)
                THEN RAISE(ABORT,'Invalid previous attempt') END;
        END;
        CREATE TRIGGER message_identity_immutable BEFORE UPDATE OF id,conversation_id,sequence,role,reply_to_user_id,supersedes_message_id ON chat_message
        BEGIN SELECT RAISE(ABORT,'Message identity is immutable'); END;
        CREATE TRIGGER terminal_message_immutable BEFORE UPDATE ON chat_message WHEN OLD.status NOT IN ('Queued','Running')
        BEGIN SELECT RAISE(ABORT,'Terminal message is immutable'); END;
        CREATE TABLE generation_task (
            id TEXT PRIMARY KEY NOT NULL,
            conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
            kind TEXT NOT NULL CHECK(kind IN ('Reply','Title')),
            answer_message_id TEXT,
            status TEXT NOT NULL CHECK(status IN ('Queued','Running','Completed','Stopped','Failed','Interrupted')),
            revision INTEGER NOT NULL CHECK(revision>=0),
            configuration_json TEXT NOT NULL CHECK(json_valid(configuration_json)),
            input_ids_json TEXT NOT NULL CHECK(json_valid(input_ids_json)),
            expected_title_revision INTEGER,
            error_code TEXT,
            actual_model_id TEXT,
            FOREIGN KEY(answer_message_id,conversation_id) REFERENCES chat_message(id,conversation_id),
            UNIQUE(answer_message_id),
            CHECK((kind='Reply' AND answer_message_id IS NOT NULL AND expected_title_revision IS NULL)
                OR (kind='Title' AND answer_message_id IS NULL AND expected_title_revision>=0))
        );
        CREATE UNIQUE INDEX one_active_generation_kind ON generation_task(conversation_id,kind) WHERE status IN ('Queued','Running');
        CREATE TRIGGER generation_answer_role BEFORE INSERT ON generation_task WHEN NEW.kind='Reply'
        BEGIN
            SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM chat_message WHERE id=NEW.answer_message_id
                AND conversation_id=NEW.conversation_id AND role='Assistant' AND status=NEW.status AND revision=NEW.revision)
                THEN RAISE(ABORT,'Invalid generation answer') END;
        END;
        CREATE TRIGGER generation_identity_immutable BEFORE UPDATE OF id,conversation_id,kind,answer_message_id,configuration_json,input_ids_json,expected_title_revision ON generation_task
        BEGIN SELECT RAISE(ABORT,'Generation identity is immutable'); END;
        CREATE TRIGGER terminal_generation_immutable BEFORE UPDATE ON generation_task WHEN OLD.status NOT IN ('Queued','Running')
        BEGIN SELECT RAISE(ABORT,'Terminal generation is immutable'); END;
        CREATE TABLE active_ai_configuration (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            configuration_json TEXT NOT NULL CHECK(json_valid(configuration_json))
        );
        """), new(3, """
        ALTER TABLE app_selection ADD COLUMN kind TEXT NOT NULL DEFAULT 'Overview' CHECK(kind IN ('Overview','Conversation'));
        ALTER TABLE app_selection ADD COLUMN target_id TEXT REFERENCES conversation(id);
        CREATE TRIGGER selection_detail_insert BEFORE INSERT ON app_selection
        WHEN (NEW.kind='Overview' AND NEW.target_id IS NOT NULL) OR
             (NEW.kind='Conversation' AND (NEW.area!='Explore' OR NEW.target_id IS NULL OR NOT EXISTS(
                SELECT 1 FROM conversation WHERE id=NEW.target_id AND workspace_id=NEW.workspace_id)))
        BEGIN SELECT RAISE(ABORT,'Invalid selection detail'); END;
        CREATE TRIGGER selection_detail_update BEFORE UPDATE ON app_selection
        WHEN (NEW.kind='Overview' AND NEW.target_id IS NOT NULL) OR
             (NEW.kind='Conversation' AND (NEW.area!='Explore' OR NEW.target_id IS NULL OR NOT EXISTS(
                SELECT 1 FROM conversation WHERE id=NEW.target_id AND workspace_id=NEW.workspace_id)))
        BEGIN SELECT RAISE(ABORT,'Invalid selection detail'); END;
        """)]);
}
