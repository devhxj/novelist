export namespace app {
	
	export class ChatInput {
	    session_id: string;
	    novel_id: number;
	    message: string;
	    provider_name: string;
	    model_id: string;
	    reasoning_effort: string;
	
	    static createFrom(source: any = {}) {
	        return new ChatInput(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.session_id = source["session_id"];
	        this.novel_id = source["novel_id"];
	        this.message = source["message"];
	        this.provider_name = source["provider_name"];
	        this.model_id = source["model_id"];
	        this.reasoning_effort = source["reasoning_effort"];
	    }
	}
	export class ChatResult {
	    session_id: string;
	    turn_id: number;
	    final_text: string;
	
	    static createFrom(source: any = {}) {
	        return new ChatResult(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.session_id = source["session_id"];
	        this.turn_id = source["turn_id"];
	        this.final_text = source["final_text"];
	    }
	}
	export class CreateChapterInput {
	    novel_id: number;
	    title: string;
	
	    static createFrom(source: any = {}) {
	        return new CreateChapterInput(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.novel_id = source["novel_id"];
	        this.title = source["title"];
	    }
	}
	export class CreateNovelInput {
	    title: string;
	    description?: string;
	
	    static createFrom(source: any = {}) {
	        return new CreateNovelInput(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.title = source["title"];
	        this.description = source["description"];
	    }
	}
	export class SaveContentInput {
	    novel_id: number;
	    path: string;
	    content: string;
	
	    static createFrom(source: any = {}) {
	        return new SaveContentInput(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.novel_id = source["novel_id"];
	        this.path = source["path"];
	        this.content = source["content"];
	    }
	}
	export class SaveSettingsInput {
	
	
	    static createFrom(source: any = {}) {
	        return new SaveSettingsInput(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	
	    }
	}
	export class SessionMeta {
	    session_id: string;
	    title: string;
	    model: string;
	    updated_at: string;
	
	    static createFrom(source: any = {}) {
	        return new SessionMeta(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.session_id = source["session_id"];
	        this.title = source["title"];
	        this.model = source["model"];
	        this.updated_at = source["updated_at"];
	    }
	}
	export class SetActiveNovelInput {
	    novel_id: number;
	
	    static createFrom(source: any = {}) {
	        return new SetActiveNovelInput(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.novel_id = source["novel_id"];
	    }
	}

}

export namespace chapter {
	
	export class Chapter {
	    id: number;
	    novel_id: number;
	    chapter_number: number;
	    title: string;
	    summary: string;
	    word_count: number;
	    // Go type: time
	    created_at: any;
	    // Go type: time
	    updated_at: any;
	    file_path: string;
	
	    static createFrom(source: any = {}) {
	        return new Chapter(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.id = source["id"];
	        this.novel_id = source["novel_id"];
	        this.chapter_number = source["chapter_number"];
	        this.title = source["title"];
	        this.summary = source["summary"];
	        this.word_count = source["word_count"];
	        this.created_at = this.convertValues(source["created_at"], null);
	        this.updated_at = this.convertValues(source["updated_at"], null);
	        this.file_path = source["file_path"];
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}

}

export namespace config {
	
	export class AppSettings {
	    ID: number;
	    last_novel_id: number;
	
	    static createFrom(source: any = {}) {
	        return new AppSettings(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.ID = source["ID"];
	        this.last_novel_id = source["last_novel_id"];
	    }
	}

}

export namespace llm {
	
	export class AvailableModel {
	    Key: string;
	    ProviderName: string;
	    ModelName: string;
	    ContextWindow: number;
	    MaxOutputTokens: number;
	    ReasoningLevels: string[];
	    SupportsVision: boolean;
	
	    static createFrom(source: any = {}) {
	        return new AvailableModel(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.Key = source["Key"];
	        this.ProviderName = source["ProviderName"];
	        this.ModelName = source["ModelName"];
	        this.ContextWindow = source["ContextWindow"];
	        this.MaxOutputTokens = source["MaxOutputTokens"];
	        this.ReasoningLevels = source["ReasoningLevels"];
	        this.SupportsVision = source["SupportsVision"];
	    }
	}
	export class ModelInfo {
	    id: string;
	    name: string;
	    context_window: number;
	    max_output_tokens: number;
	    reasoning_levels?: string[];
	    supports_vision: boolean;
	
	    static createFrom(source: any = {}) {
	        return new ModelInfo(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.id = source["id"];
	        this.name = source["name"];
	        this.context_window = source["context_window"];
	        this.max_output_tokens = source["max_output_tokens"];
	        this.reasoning_levels = source["reasoning_levels"];
	        this.supports_vision = source["supports_vision"];
	    }
	}
	export class ProviderView {
	    key: string;
	    name: string;
	    chat_url: string;
	    api_key: string;
	    source: string;
	    builtin_models: ModelInfo[];
	    custom_models: ModelInfo[];
	
	    static createFrom(source: any = {}) {
	        return new ProviderView(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.key = source["key"];
	        this.name = source["name"];
	        this.chat_url = source["chat_url"];
	        this.api_key = source["api_key"];
	        this.source = source["source"];
	        this.builtin_models = this.convertValues(source["builtin_models"], ModelInfo);
	        this.custom_models = this.convertValues(source["custom_models"], ModelInfo);
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}
	export class LLMConfigView {
	    providers: ProviderView[];
	
	    static createFrom(source: any = {}) {
	        return new LLMConfigView(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.providers = this.convertValues(source["providers"], ProviderView);
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}
	

}

export namespace novel {
	
	export class Novel {
	    id: number;
	    title: string;
	    genre: string;
	    description: string;
	    dir_path: string;
	    // Go type: time
	    created_at: any;
	    // Go type: time
	    updated_at: any;
	
	    static createFrom(source: any = {}) {
	        return new Novel(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.id = source["id"];
	        this.title = source["title"];
	        this.genre = source["genre"];
	        this.description = source["description"];
	        this.dir_path = source["dir_path"];
	        this.created_at = this.convertValues(source["created_at"], null);
	        this.updated_at = this.convertValues(source["updated_at"], null);
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}

}

export namespace session {
	
	export class Message {
	    id: number;
	    session_id: string;
	    turn_id: number;
	    role: string;
	    content: string;
	    token_count: number;
	    extra_metadata?: string;
	    version: number;
	    to_api: boolean;
	    to_frontend: boolean;
	    event_type?: string;
	    agent_type: string;
	    parent_turn_id?: number;
	    // Go type: time
	    created_at: any;
	
	    static createFrom(source: any = {}) {
	        return new Message(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.id = source["id"];
	        this.session_id = source["session_id"];
	        this.turn_id = source["turn_id"];
	        this.role = source["role"];
	        this.content = source["content"];
	        this.token_count = source["token_count"];
	        this.extra_metadata = source["extra_metadata"];
	        this.version = source["version"];
	        this.to_api = source["to_api"];
	        this.to_frontend = source["to_frontend"];
	        this.event_type = source["event_type"];
	        this.agent_type = source["agent_type"];
	        this.parent_turn_id = source["parent_turn_id"];
	        this.created_at = this.convertValues(source["created_at"], null);
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}

}

export namespace storage {
	
	export class PageResult_novel_app_SessionMeta_ {
	    items: app.SessionMeta[];
	    total: number;
	    page: number;
	    size: number;
	    total_pages: number;
	
	    static createFrom(source: any = {}) {
	        return new PageResult_novel_app_SessionMeta_(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.items = this.convertValues(source["items"], app.SessionMeta);
	        this.total = source["total"];
	        this.page = source["page"];
	        this.size = source["size"];
	        this.total_pages = source["total_pages"];
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}

}

