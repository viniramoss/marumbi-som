import type { components } from '../types/api';

// Nomes curtos para os tipos gerados do OpenAPI do Hub (seção 7.3: nunca escritos à mão).
type Schemas = components['schemas'];

export type StoreSummary = Schemas['StoreSummary'];
export type StoreDetail = Schemas['StoreDetail'];
export type StoreAlert = Schemas['StoreAlert'];
export type StoreTrack = Schemas['StoreTrack'];
export type NowPlayingInfo = Schemas['NowPlayingInfo'];
export type ScheduleInput = Schemas['ScheduleInput'];
export type StoreInput = Schemas['StoreInput'];
export type CommandOutcome = Schemas['CommandOutcome'];
export type EnrollCodeResult = Schemas['EnrollCodeResult'];
export type HistoryEntry = Schemas['HistoryEntry'];
export type EventEntry = Schemas['EventEntry'];
export type Track = Schemas['TrackDto'];
export type IngestionJob = Schemas['IngestionJob'];
export type IngestionStatus = Schemas['IngestionStatus'];
export type PlaylistSummary = Schemas['PlaylistSummary'];
export type PlaylistDetail = Schemas['PlaylistDetail'];
export type PlaylistItem = Schemas['PlaylistItemDetail'];
export type PlaylistUsage = Schemas['PlaylistUsage'];
export type ItemInput = Schemas['ItemInput'];
export type SettingsInfo = Schemas['SettingsInfo'];
export type SessionInfo = Schemas['SessionInfo'];
export type PanelMessage = Schemas['PanelMessage'];
