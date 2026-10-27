namespace Mindweave.Infrastructure.Storage;

// Additive schema 6. S3 request format 6 is an unrelated protocol version.
internal static class SummarySchema
{
    internal const string Sql = """
        CREATE TABLE node_naming_group (
            workspace_id TEXT NOT NULL REFERENCES workspace(id),
            group_key TEXT NOT NULL,
            epoch INTEGER NOT NULL CHECK(typeof(epoch)='integer' AND epoch>=0),
            pending_operation_id TEXT,
            pending_snapshot_json TEXT CHECK(pending_snapshot_json IS NULL OR json_valid(pending_snapshot_json)),
            pending_configuration_json TEXT CHECK(pending_configuration_json IS NULL OR json_valid(pending_configuration_json)),
            pending_at TEXT,
            PRIMARY KEY(workspace_id,group_key),
            CHECK((pending_operation_id IS NULL AND pending_snapshot_json IS NULL AND pending_configuration_json IS NULL AND pending_at IS NULL)
                OR (pending_operation_id IS NOT NULL AND pending_snapshot_json IS NOT NULL AND pending_configuration_json IS NOT NULL AND pending_at IS NOT NULL))
        );
        CREATE TABLE summary_node (
            id TEXT PRIMARY KEY NOT NULL,
            workspace_id TEXT NOT NULL REFERENCES workspace(id),
            conversation_id TEXT NOT NULL UNIQUE REFERENCES conversation(id),
            current_version_id TEXT NOT NULL,
            candidate_version_id TEXT,
            last_version_ordinal INTEGER NOT NULL CHECK(typeof(last_version_ordinal)='integer' AND last_version_ordinal>0),
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0),
            base_title TEXT NOT NULL CHECK(length(trim(base_title))>0),
            display_name TEXT NOT NULL CHECK(length(trim(display_name))>0),
            group_key TEXT NOT NULL,
            name_is_manual INTEGER NOT NULL CHECK(name_is_manual IN (0,1)),
            name_revision INTEGER NOT NULL CHECK(typeof(name_revision)='integer' AND name_revision>=0),
            created_at TEXT NOT NULL,
            UNIQUE(id,workspace_id),
            FOREIGN KEY(current_version_id,id) REFERENCES summary_version(id,node_id) DEFERRABLE INITIALLY DEFERRED,
            FOREIGN KEY(candidate_version_id,id) REFERENCES summary_version(id,node_id) DEFERRABLE INITIALLY DEFERRED,
            CHECK(candidate_version_id IS NULL OR candidate_version_id!=current_version_id)
        );
        CREATE INDEX summary_node_group ON summary_node(workspace_id,group_key,id);
        CREATE TRIGGER summary_node_owner BEFORE INSERT ON summary_node
        WHEN NOT EXISTS(SELECT 1 FROM conversation WHERE id=NEW.conversation_id AND workspace_id=NEW.workspace_id)
        BEGIN SELECT RAISE(ABORT,'Summary conversation ownership mismatch'); END;
        CREATE TRIGGER summary_node_identity_immutable BEFORE UPDATE OF id,workspace_id,conversation_id,created_at ON summary_node
        BEGIN SELECT RAISE(ABORT,'Summary node identity is immutable'); END;
        CREATE TABLE summary_version_identity (
            id TEXT PRIMARY KEY NOT NULL,
            node_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK(typeof(ordinal)='integer' AND ordinal>0),
            origin TEXT NOT NULL CHECK(origin IN ('Generated','Manual')),
            created_at TEXT NOT NULL,
            UNIQUE(id,node_id), UNIQUE(node_id,ordinal)
        );
        CREATE TRIGGER summary_version_ordinal_guard BEFORE INSERT ON summary_version_identity
        WHEN EXISTS(SELECT 1 FROM summary_node WHERE id=NEW.node_id AND last_version_ordinal>=NEW.ordinal)
        BEGIN SELECT RAISE(ABORT,'Summary version ordinal must advance'); END;
        CREATE TRIGGER summary_version_ordinal_advance AFTER INSERT ON summary_version_identity
        BEGIN UPDATE summary_node SET last_version_ordinal=NEW.ordinal WHERE id=NEW.node_id; END;
        CREATE TRIGGER summary_version_identity_immutable BEFORE UPDATE ON summary_version_identity
        BEGIN SELECT RAISE(ABORT,'Summary version identity is immutable'); END;
        CREATE TABLE summary_version (
            id TEXT PRIMARY KEY NOT NULL,
            node_id TEXT NOT NULL REFERENCES summary_node(id) DEFERRABLE INITIALLY DEFERRED,
            title TEXT NOT NULL CHECK(length(trim(title))>0),
            markdown TEXT NOT NULL CHECK(length(trim(markdown))>0),
            baseline_version_id TEXT,
            requested_model_id TEXT,
            actual_model_id TEXT,
            content_hash TEXT NOT NULL CHECK(length(content_hash)=64),
            UNIQUE(id,node_id),
            FOREIGN KEY(id,node_id) REFERENCES summary_version_identity(id,node_id),
            FOREIGN KEY(baseline_version_id,node_id) REFERENCES summary_version_identity(id,node_id) DEFERRABLE INITIALLY DEFERRED,
            CHECK(baseline_version_id IS NULL OR baseline_version_id!=id)
        );
        CREATE TRIGGER summary_version_immutable BEFORE UPDATE ON summary_version
        BEGIN SELECT RAISE(ABORT,'Summary content is immutable'); END;
        CREATE TABLE summary_version_source (
            version_id TEXT PRIMARY KEY NOT NULL REFERENCES summary_version(id) ON DELETE CASCADE,
            coverage_json TEXT NOT NULL CHECK(json_valid(coverage_json))
        );
        CREATE TRIGGER summary_version_source_immutable BEFORE UPDATE ON summary_version_source
        BEGIN SELECT RAISE(ABORT,'Summary coverage is immutable'); END;
        CREATE TABLE summary_version_message (
            version_id TEXT NOT NULL REFERENCES summary_version(id) ON DELETE CASCADE,
            scope TEXT NOT NULL CHECK(scope IN ('Own','FrozenAncestor')),
            ordinal INTEGER NOT NULL CHECK(typeof(ordinal)='integer' AND ordinal>=0),
            message_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            PRIMARY KEY(version_id,scope,ordinal),
            FOREIGN KEY(message_id,conversation_id) REFERENCES chat_message(id,conversation_id)
        );
        CREATE TRIGGER summary_version_message_immutable BEFORE UPDATE ON summary_version_message
        BEGIN SELECT RAISE(ABORT,'Summary source reference is immutable'); END;
        CREATE TABLE summary_read_state (
            version_id TEXT PRIMARY KEY NOT NULL REFERENCES summary_version(id) ON DELETE CASCADE,
            epoch INTEGER NOT NULL CHECK(typeof(epoch)='integer' AND epoch>=0),
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0),
            is_read INTEGER NOT NULL CHECK(is_read IN (0,1)),
            suppress_automatic INTEGER NOT NULL CHECK(suppress_automatic IN (0,1)),
            text_hash TEXT NOT NULL,
            text_length INTEGER NOT NULL CHECK(typeof(text_length)='integer' AND text_length>=0),
            seen_json TEXT NOT NULL CHECK(json_valid(seen_json) AND json_type(seen_json)='array'),
            visible_dwell_ticks INTEGER NOT NULL CHECK(typeof(visible_dwell_ticks)='integer' AND visible_dwell_ticks>=0),
            observation_sequence INTEGER NOT NULL CHECK(typeof(observation_sequence)='integer' AND observation_sequence>=0),
            last_observation_ticks INTEGER CHECK(last_observation_ticks IS NULL OR (typeof(last_observation_ticks)='integer' AND last_observation_ticks>=0)),
            was_active_visible INTEGER NOT NULL CHECK(was_active_visible IN (0,1)),
            scalar_offset INTEGER,
            offset_within_block REAL,
            renderer_profile TEXT,
            CHECK((scalar_offset IS NULL AND offset_within_block IS NULL AND renderer_profile IS NULL)
                OR (scalar_offset IS NOT NULL AND scalar_offset>=0 AND offset_within_block IS NOT NULL AND offset_within_block>=0 AND renderer_profile IS NOT NULL))
        );
        CREATE TABLE summary_edit_draft (
            node_id TEXT PRIMARY KEY NOT NULL REFERENCES summary_node(id),
            base_version_id TEXT NOT NULL,
            epoch INTEGER NOT NULL CHECK(typeof(epoch)='integer' AND epoch>=0),
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0),
            title TEXT,
            markdown TEXT,
            is_dirty INTEGER NOT NULL CHECK(is_dirty IN (0,1)),
            FOREIGN KEY(base_version_id,node_id) REFERENCES summary_version_identity(id,node_id) DEFERRABLE INITIALLY DEFERRED,
            CHECK((title IS NULL AND markdown IS NULL AND is_dirty=0) OR (title IS NOT NULL AND markdown IS NOT NULL))
        );
        CREATE TABLE knowledge_summary_job (
            id TEXT PRIMARY KEY NOT NULL,
            operation_id TEXT NOT NULL UNIQUE,
            workspace_id TEXT NOT NULL REFERENCES workspace(id),
            conversation_id TEXT NOT NULL REFERENCES conversation(id),
            planned_node_id TEXT NOT NULL,
            baseline_version_id TEXT,
            baseline_node_revision INTEGER NOT NULL CHECK(typeof(baseline_node_revision)='integer' AND baseline_node_revision>=0),
            creates_node INTEGER NOT NULL CHECK(creates_node IN (0,1)),
            status TEXT NOT NULL CHECK(status IN ('Queued','Running','Completed','Stopped','Failed','Interrupted')),
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0),
            input_plan_json TEXT NOT NULL CHECK(json_valid(input_plan_json)),
            accepted_at TEXT NOT NULL,
            error_code TEXT,
            actual_model_id TEXT,
            output_version_id TEXT,
            output_is_candidate INTEGER CHECK(output_is_candidate IN (0,1)),
            output_content_hash TEXT CHECK(output_content_hash IS NULL OR length(output_content_hash)=64),
            FOREIGN KEY(baseline_version_id,planned_node_id) REFERENCES summary_version_identity(id,node_id) DEFERRABLE INITIALLY DEFERRED,
            FOREIGN KEY(output_version_id,planned_node_id) REFERENCES summary_version_identity(id,node_id) DEFERRABLE INITIALLY DEFERRED,
            CHECK((creates_node=1 AND baseline_version_id IS NULL AND baseline_node_revision=0) OR (creates_node=0 AND baseline_version_id IS NOT NULL)),
            CHECK((status='Completed' AND output_version_id IS NOT NULL AND output_is_candidate IS NOT NULL AND output_content_hash IS NOT NULL)
                OR (status!='Completed' AND output_version_id IS NULL AND output_is_candidate IS NULL AND output_content_hash IS NULL))
        );
        CREATE UNIQUE INDEX one_active_knowledge_summary ON knowledge_summary_job(conversation_id) WHERE status IN ('Queued','Running');
        CREATE INDEX knowledge_summary_target ON knowledge_summary_job(planned_node_id,status);
        CREATE TRIGGER knowledge_summary_owner BEFORE INSERT ON knowledge_summary_job
        WHEN NOT EXISTS(SELECT 1 FROM conversation WHERE id=NEW.conversation_id AND workspace_id=NEW.workspace_id)
        BEGIN SELECT RAISE(ABORT,'Summary job ownership mismatch'); END;
        CREATE TRIGGER knowledge_summary_identity_immutable BEFORE UPDATE OF id,operation_id,workspace_id,conversation_id,planned_node_id,baseline_version_id,baseline_node_revision,creates_node,input_plan_json,accepted_at ON knowledge_summary_job
        BEGIN SELECT RAISE(ABORT,'Summary job identity is immutable'); END;
        CREATE TRIGGER knowledge_summary_terminal_immutable BEFORE UPDATE ON knowledge_summary_job WHEN OLD.status NOT IN ('Queued','Running')
        BEGIN SELECT RAISE(ABORT,'Terminal summary job is immutable'); END;
        CREATE TRIGGER knowledge_summary_revision BEFORE UPDATE ON knowledge_summary_job WHEN NEW.revision!=OLD.revision+1
        BEGIN SELECT RAISE(ABORT,'Summary revision must advance by one'); END;
        CREATE TABLE knowledge_summary_source (
            task_id TEXT NOT NULL REFERENCES knowledge_summary_job(id) ON DELETE CASCADE,
            scope TEXT NOT NULL CHECK(scope IN ('OwnRow','FrozenAncestor')),
            ordinal INTEGER NOT NULL CHECK(typeof(ordinal)='integer' AND ordinal>=0),
            message_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            reference_json TEXT NOT NULL CHECK(json_valid(reference_json)),
            PRIMARY KEY(task_id,scope,ordinal),
            UNIQUE(task_id,message_id),
            FOREIGN KEY(message_id,conversation_id) REFERENCES chat_message(id,conversation_id)
        );
        CREATE TRIGGER knowledge_summary_source_immutable BEFORE UPDATE ON knowledge_summary_source
        BEGIN SELECT RAISE(ABORT,'Summary input reference is immutable'); END;
        CREATE TABLE knowledge_summary_prepared (
            task_id TEXT PRIMARY KEY NOT NULL REFERENCES knowledge_summary_job(id) ON DELETE CASCADE,
            format_version INTEGER NOT NULL,
            configuration_json TEXT NOT NULL CHECK(json_valid(configuration_json)),
            request_hash TEXT NOT NULL CHECK(length(request_hash)=64)
        );
        CREATE TABLE knowledge_summary_material (
            task_id TEXT NOT NULL REFERENCES knowledge_summary_prepared(task_id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK(typeof(ordinal)='integer' AND ordinal>=0),
            kind TEXT NOT NULL CHECK(kind IN ('Instruction','OwnMessage','FrozenAncestor','DirectQuote')),
            transport_index INTEGER NOT NULL CHECK(transport_index>=0),
            json_path TEXT NOT NULL,
            message_id TEXT,
            material_hash TEXT NOT NULL CHECK(length(material_hash)=64),
            PRIMARY KEY(task_id,ordinal)
        );
        CREATE TRIGGER knowledge_summary_prepared_immutable BEFORE UPDATE ON knowledge_summary_prepared
        BEGIN SELECT RAISE(ABORT,'Prepared summary request is immutable'); END;
        CREATE TRIGGER knowledge_summary_material_immutable BEFORE UPDATE ON knowledge_summary_material
        BEGIN SELECT RAISE(ABORT,'Prepared summary material is immutable'); END;
        CREATE TABLE summary_selection (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            workspace_id TEXT,
            node_id TEXT,
            version_id TEXT,
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0),
            FOREIGN KEY(node_id,workspace_id) REFERENCES summary_node(id,workspace_id) DEFERRABLE INITIALLY DEFERRED,
            FOREIGN KEY(version_id,node_id) REFERENCES summary_version(id,node_id) DEFERRABLE INITIALLY DEFERRED,
            CHECK((workspace_id IS NULL AND node_id IS NULL AND version_id IS NULL) OR
                (workspace_id IS NOT NULL AND node_id IS NOT NULL AND version_id IS NOT NULL))
        );
        CREATE TABLE node_naming_job (
            id TEXT PRIMARY KEY NOT NULL,
            operation_id TEXT NOT NULL UNIQUE,
            workspace_id TEXT NOT NULL REFERENCES workspace(id),
            group_key TEXT NOT NULL,
            group_epoch INTEGER NOT NULL CHECK(typeof(group_epoch)='integer' AND group_epoch>=0),
            snapshot_json TEXT NOT NULL CHECK(json_valid(snapshot_json)),
            configuration_json TEXT NOT NULL CHECK(json_valid(configuration_json)),
            status TEXT NOT NULL CHECK(status IN ('Queued','Running','Completed','Stopped','Failed','Interrupted')),
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0),
            accepted_at TEXT NOT NULL,
            error_code TEXT,
            prepared_format_version INTEGER,
            protocol_version TEXT,
            request_hash TEXT,
            output_hash TEXT,
            changed_node_ids_json TEXT CHECK(changed_node_ids_json IS NULL OR json_valid(changed_node_ids_json))
        );
        CREATE UNIQUE INDEX one_active_node_naming_group ON node_naming_job(workspace_id,group_key) WHERE status IN ('Queued','Running');
        CREATE TABLE node_naming_member (
            task_id TEXT NOT NULL REFERENCES node_naming_job(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK(typeof(ordinal)='integer' AND ordinal>=0),
            node_id TEXT NOT NULL,
            current_version_id TEXT NOT NULL,
            name_revision INTEGER NOT NULL CHECK(typeof(name_revision)='integer' AND name_revision>=0),
            name_is_manual INTEGER NOT NULL CHECK(name_is_manual IN (0,1)),
            content_hash TEXT NOT NULL CHECK(length(content_hash)=64),
            PRIMARY KEY(task_id,ordinal), UNIQUE(task_id,node_id),
            FOREIGN KEY(current_version_id,node_id) REFERENCES summary_version_identity(id,node_id)
        );
        CREATE TRIGGER node_naming_identity_immutable BEFORE UPDATE OF id,operation_id,workspace_id,group_key,group_epoch,snapshot_json,configuration_json,accepted_at ON node_naming_job
        BEGIN SELECT RAISE(ABORT,'Naming job identity is immutable'); END;
        CREATE TRIGGER node_naming_terminal_immutable BEFORE UPDATE ON node_naming_job WHEN OLD.status NOT IN ('Queued','Running')
        BEGIN SELECT RAISE(ABORT,'Terminal naming job is immutable'); END;
        CREATE TRIGGER node_naming_revision BEFORE UPDATE ON node_naming_job WHEN NEW.revision!=OLD.revision+1
        BEGIN SELECT RAISE(ABORT,'Naming revision must advance by one'); END;
        CREATE TRIGGER node_naming_member_immutable BEFORE UPDATE ON node_naming_member
        BEGIN SELECT RAISE(ABORT,'Naming member identity is immutable'); END;
        CREATE TRIGGER summary_group_insert AFTER INSERT ON summary_node
        BEGIN
            INSERT INTO node_naming_group(workspace_id,group_key,epoch) VALUES(NEW.workspace_id,NEW.group_key,1)
                ON CONFLICT(workspace_id,group_key) DO UPDATE SET epoch=epoch+1;
        END;
        CREATE TRIGGER summary_group_change AFTER UPDATE OF current_version_id,group_key,name_revision,name_is_manual ON summary_node
        WHEN NEW.current_version_id!=OLD.current_version_id OR NEW.group_key!=OLD.group_key OR NEW.name_revision!=OLD.name_revision OR NEW.name_is_manual!=OLD.name_is_manual
        BEGIN
            UPDATE node_naming_group SET epoch=epoch+1 WHERE workspace_id=OLD.workspace_id AND group_key=OLD.group_key;
            INSERT INTO node_naming_group(workspace_id,group_key,epoch) SELECT NEW.workspace_id,NEW.group_key,1 WHERE NEW.group_key!=OLD.group_key
                ON CONFLICT(workspace_id,group_key) DO UPDATE SET epoch=epoch+1;
        END;
        CREATE TRIGGER summary_group_delete AFTER DELETE ON summary_node
        BEGIN UPDATE node_naming_group SET epoch=epoch+1 WHERE workspace_id=OLD.workspace_id AND group_key=OLD.group_key; END;
        """;
}
