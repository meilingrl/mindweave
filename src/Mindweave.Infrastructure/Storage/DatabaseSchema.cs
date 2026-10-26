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
        """), new(4, """
        CREATE TABLE branch_source (
            child_conversation_id TEXT PRIMARY KEY NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
            operation_id TEXT NOT NULL UNIQUE,
            parent_conversation_id TEXT NOT NULL REFERENCES conversation(id),
            message_id TEXT NOT NULL,
            message_revision INTEGER NOT NULL CHECK(message_revision>=0),
            source_body_hash TEXT NOT NULL CHECK(length(source_body_hash)=64),
            quote TEXT NOT NULL CHECK(length(trim(quote))>0),
            anchor_version INTEGER NOT NULL CHECK(anchor_version=1),
            renderer_profile TEXT NOT NULL,
            anchor_payload TEXT NOT NULL CHECK(json_valid(anchor_payload) AND json_type(anchor_payload)='object'),
            FOREIGN KEY(message_id,parent_conversation_id) REFERENCES chat_message(id,conversation_id),
            CHECK(child_conversation_id!=parent_conversation_id)
        );
        CREATE INDEX branch_parent ON branch_source(parent_conversation_id,message_id);
        CREATE TRIGGER branch_source_immutable BEFORE UPDATE ON branch_source
        BEGIN SELECT RAISE(ABORT,'Branch source is immutable'); END;
        CREATE TABLE frozen_context_range (
            child_conversation_id TEXT NOT NULL REFERENCES branch_source(child_conversation_id) ON DELETE CASCADE,
            range_order INTEGER NOT NULL CHECK(range_order>=0),
            ancestor_conversation_id TEXT NOT NULL REFERENCES conversation(id),
            PRIMARY KEY(child_conversation_id,range_order),
            UNIQUE(child_conversation_id,ancestor_conversation_id),
            UNIQUE(child_conversation_id,range_order,ancestor_conversation_id)
        );
        CREATE TABLE frozen_context_message (
            child_conversation_id TEXT NOT NULL,
            range_order INTEGER NOT NULL,
            ancestor_conversation_id TEXT NOT NULL,
            message_order INTEGER NOT NULL CHECK(message_order>=0),
            message_id TEXT NOT NULL,
            PRIMARY KEY(child_conversation_id,range_order,message_order),
            UNIQUE(child_conversation_id,message_id),
            FOREIGN KEY(child_conversation_id,range_order,ancestor_conversation_id)
                REFERENCES frozen_context_range(child_conversation_id,range_order,ancestor_conversation_id) ON DELETE CASCADE,
            FOREIGN KEY(message_id,ancestor_conversation_id) REFERENCES chat_message(id,conversation_id)
        );
        CREATE TRIGGER frozen_range_immutable BEFORE UPDATE ON frozen_context_range
        BEGIN SELECT RAISE(ABORT,'Frozen range is immutable'); END;
        CREATE TRIGGER frozen_message_immutable BEFORE UPDATE ON frozen_context_message
        BEGIN SELECT RAISE(ABORT,'Frozen message is immutable'); END;
        CREATE TABLE conversation_read_state (
            conversation_id TEXT PRIMARY KEY NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
            message_id TEXT,
            source_start INTEGER,
            offset_within_block REAL,
            format_version INTEGER,
            renderer_profile TEXT,
            revision INTEGER NOT NULL CHECK(revision>0),
            FOREIGN KEY(message_id,conversation_id) REFERENCES chat_message(id,conversation_id),
            CHECK((message_id IS NULL AND source_start IS NULL AND offset_within_block IS NULL AND format_version IS NULL AND renderer_profile IS NULL)
                OR (message_id IS NOT NULL AND source_start>=0 AND offset_within_block>=0 AND format_version=1 AND renderer_profile IS NOT NULL))
        );
        """), new(5, """
        CREATE TABLE reply_context_plan (
            task_id TEXT PRIMARY KEY NOT NULL REFERENCES generation_task(id) ON DELETE CASCADE,
            plan_json TEXT NOT NULL CHECK(json_valid(plan_json))
        );
        CREATE TRIGGER context_plan_reply BEFORE INSERT ON reply_context_plan
        WHEN NOT EXISTS(SELECT 1 FROM generation_task WHERE id=NEW.task_id AND kind='Reply' AND status IN ('Queued','Running'))
        BEGIN SELECT RAISE(ABORT,'Context requires active reply'); END;
        CREATE TRIGGER context_plan_immutable BEFORE UPDATE ON reply_context_plan
        BEGIN SELECT RAISE(ABORT,'Context plan is immutable'); END;
        CREATE TABLE context_plan_chunk (
            task_id TEXT NOT NULL REFERENCES reply_context_plan(task_id) ON DELETE CASCADE,
            cache_key TEXT NOT NULL,
            chunk_json TEXT NOT NULL CHECK(json_valid(chunk_json)),
            PRIMARY KEY(task_id,cache_key)
        );
        CREATE TRIGGER context_chunk_immutable BEFORE UPDATE ON context_plan_chunk
        BEGIN SELECT RAISE(ABORT,'Context chunk is immutable'); END;
        CREATE TABLE context_compression_attempt (
            id TEXT PRIMARY KEY NOT NULL,
            source_conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
            cache_key TEXT NOT NULL,
            configuration_json TEXT NOT NULL CHECK(json_valid(configuration_json)),
            chunk_json TEXT NOT NULL CHECK(json_valid(chunk_json)),
            status TEXT NOT NULL CHECK(status IN ('Queued','Running','Completed','Stopped','Failed','Interrupted')),
            revision INTEGER NOT NULL CHECK(revision>=0),
            payload TEXT NOT NULL,
            finish_reason TEXT,
            error_code TEXT,
            actual_model_id TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(id,cache_key,source_conversation_id),
            UNIQUE(id,source_conversation_id)
        );
        CREATE TABLE context_compression_source (
            attempt_id TEXT NOT NULL,
            source_conversation_id TEXT NOT NULL,
            slice_order INTEGER NOT NULL CHECK(slice_order>=0),
            message_id TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK(revision>=0),
            body_hash TEXT NOT NULL,
            start INTEGER NOT NULL CHECK(start>=0),
            length INTEGER NOT NULL CHECK(length>=0),
            PRIMARY KEY(attempt_id,slice_order),
            FOREIGN KEY(attempt_id,source_conversation_id) REFERENCES context_compression_attempt(id,source_conversation_id) ON DELETE CASCADE,
            FOREIGN KEY(message_id,source_conversation_id) REFERENCES chat_message(id,conversation_id)
        );
        CREATE TRIGGER compression_source_immutable BEFORE UPDATE ON context_compression_source
        BEGIN SELECT RAISE(ABORT,'Compression sources are immutable'); END;
        CREATE TABLE context_compression_dependency (
            task_id TEXT NOT NULL,
            cache_key TEXT NOT NULL,
            attempt_id TEXT NOT NULL UNIQUE REFERENCES context_compression_attempt(id),
            PRIMARY KEY(task_id,cache_key,attempt_id),
            FOREIGN KEY(task_id,cache_key) REFERENCES context_plan_chunk(task_id,cache_key) ON DELETE CASCADE
        );
        CREATE TRIGGER compression_dependency_valid BEFORE INSERT ON context_compression_dependency
        WHEN NOT EXISTS(SELECT 1 FROM generation_task g
            JOIN context_plan_chunk c ON c.task_id=g.id AND c.cache_key=NEW.cache_key
            JOIN context_compression_attempt a ON a.id=NEW.attempt_id AND a.cache_key=NEW.cache_key
            WHERE g.id=NEW.task_id AND g.kind='Reply' AND g.status IN ('Queued','Running')
                AND a.configuration_json=g.configuration_json AND a.chunk_json=c.chunk_json
                AND (a.source_conversation_id=g.conversation_id OR EXISTS(SELECT 1 FROM frozen_context_range r
                    WHERE r.child_conversation_id=g.conversation_id AND r.ancestor_conversation_id=a.source_conversation_id))
                AND NOT EXISTS(SELECT 1 FROM context_compression_source s WHERE s.attempt_id=a.id
                    AND s.message_id NOT IN (SELECT value FROM json_each(g.input_ids_json))))
            OR EXISTS(SELECT 1 FROM context_compression_dependency d JOIN context_compression_attempt a ON a.id=d.attempt_id
                WHERE d.task_id=NEW.task_id AND d.cache_key=NEW.cache_key AND a.status IN ('Queued','Running'))
        BEGIN SELECT RAISE(ABORT,'Compression requires an authorized active requester'); END;
        CREATE TRIGGER compression_dependency_immutable BEFORE UPDATE ON context_compression_dependency
        BEGIN SELECT RAISE(ABORT,'Compression dependency is immutable'); END;
        CREATE TRIGGER compression_dependency_active_delete BEFORE DELETE ON context_compression_dependency
        WHEN EXISTS(SELECT 1 FROM context_compression_attempt WHERE id=OLD.attempt_id AND status IN ('Queued','Running'))
        BEGIN SELECT RAISE(ABORT,'Active compression dependency cannot be deleted'); END;
        CREATE TRIGGER compression_revision BEFORE UPDATE ON context_compression_attempt
        WHEN NEW.revision<=OLD.revision OR (OLD.status='Running' AND NEW.status='Queued')
            OR (NEW.status!='Interrupted' AND NOT EXISTS(SELECT 1 FROM context_compression_dependency d
                JOIN generation_task g ON g.id=d.task_id WHERE d.attempt_id=NEW.id AND g.status IN ('Queued','Running')))
        BEGIN SELECT RAISE(ABORT,'Compression revision or requester invalid'); END;
        CREATE TRIGGER compression_identity_immutable BEFORE UPDATE OF id,source_conversation_id,cache_key,configuration_json,chunk_json,created_at ON context_compression_attempt
        BEGIN SELECT RAISE(ABORT,'Compression identity is immutable'); END;
        CREATE TRIGGER terminal_compression_immutable BEFORE UPDATE ON context_compression_attempt WHEN OLD.status NOT IN ('Queued','Running')
        BEGIN SELECT RAISE(ABORT,'Terminal compression is immutable'); END;
        CREATE TABLE context_summary_cache (
            id TEXT PRIMARY KEY NOT NULL,
            cache_key TEXT NOT NULL UNIQUE,
            source_conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
            attempt_id TEXT NOT NULL UNIQUE,
            configuration_json TEXT NOT NULL CHECK(json_valid(configuration_json)),
            chunk_json TEXT NOT NULL CHECK(json_valid(chunk_json)),
            payload TEXT NOT NULL,
            payload_hash TEXT NOT NULL,
            actual_model_id TEXT,
            completed_at TEXT NOT NULL,
            FOREIGN KEY(attempt_id,cache_key,source_conversation_id) REFERENCES context_compression_attempt(id,cache_key,source_conversation_id)
        );
        CREATE TRIGGER cache_completed_provenance BEFORE INSERT ON context_summary_cache
        WHEN NOT EXISTS(SELECT 1 FROM context_compression_attempt a
            WHERE a.id=NEW.attempt_id AND a.cache_key=NEW.cache_key AND a.status='Completed' AND a.finish_reason='stop'
                AND a.payload=NEW.payload AND a.actual_model_id IS NEW.actual_model_id AND a.updated_at=NEW.completed_at
                AND a.source_conversation_id=NEW.source_conversation_id AND a.configuration_json=NEW.configuration_json AND a.chunk_json=NEW.chunk_json)
        BEGIN SELECT RAISE(ABORT,'Cache requires matching completed source attempt'); END;
        CREATE TRIGGER context_cache_immutable BEFORE UPDATE ON context_summary_cache
        BEGIN SELECT RAISE(ABORT,'Completed cache is immutable'); END;
        CREATE TABLE prepared_reply_request (
            task_id TEXT PRIMARY KEY NOT NULL REFERENCES reply_context_plan(task_id) ON DELETE CASCADE,
            format_version INTEGER NOT NULL CHECK(format_version=1),
            configuration_json TEXT NOT NULL CHECK(json_valid(configuration_json)),
            messages_json TEXT NOT NULL CHECK(json_valid(messages_json)),
            prepared_at TEXT NOT NULL
        );
        CREATE TABLE prepared_context_use (
            task_id TEXT NOT NULL REFERENCES prepared_reply_request(task_id) ON DELETE CASCADE,
            input_index INTEGER NOT NULL CHECK(input_index>=0),
            kind TEXT NOT NULL CHECK(kind IN ('Verbatim','Summary','SourceQuote')),
            message_id TEXT REFERENCES chat_message(id),
            summary_id TEXT REFERENCES context_summary_cache(id),
            PRIMARY KEY(task_id,input_index),
            CHECK((kind='Verbatim' AND message_id IS NOT NULL AND summary_id IS NULL)
                OR (kind='Summary' AND message_id IS NULL AND summary_id IS NOT NULL)
                OR (kind='SourceQuote' AND message_id IS NOT NULL AND summary_id IS NULL))
        );
        CREATE TRIGGER prepared_request_immutable BEFORE UPDATE ON prepared_reply_request
        BEGIN SELECT RAISE(ABORT,'Prepared request is immutable'); END;
        CREATE TRIGGER prepared_use_immutable BEFORE UPDATE ON prepared_context_use
        BEGIN SELECT RAISE(ABORT,'Prepared material is immutable'); END;
        """)]);
}
