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
export type OfferSummary = Schemas['OfferSummary'];
export type OfferDetail = Schemas['OfferDetail'];
export type OfferInput = Schemas['OfferInput'];
export type OfferRule = Schemas['OfferRule'];
export type OfferAnchor = Schemas['OfferAnchor'];
export type OfferState = Schemas['OfferState'];
export type OfferStorePlays = Schemas['OfferStorePlays'];
export type StoreOffer = Schemas['StoreOffer'];
export type AnchorInput = Schemas['AnchorInput'];
export type SettingsInfo = Schemas['SettingsInfo'];
export type ReleaseInfo = Schemas['ReleaseInfo'];
export type GitHubStatus = Schemas['GitHubStatus'];
export type UpdateInfo = Schemas['UpdateInfo'];
export type SessionInfo = Schemas['SessionInfo'];
export type PanelMessage = Schemas['PanelMessage'];
