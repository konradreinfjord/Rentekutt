-- 0065 — Webhook-basert bank-autosending (Nextcom) + Soknedal Sparebank.
-- Banker kan nå ha en egen webhook-URL; matchende leads sendes dit (form-POST) i stedet for
-- å bare markeres «videresendt manuelt».
alter table public.partnere add column if not exists webhook_url text;

-- Opprett Soknedal Sparebank med Nextcom-landingsside-URL (idempotent).
insert into public.partnere (id, navn, webhook_url, auto_send)
select gen_random_uuid(),
       'Soknedal Sparebank',
       'https://soknedal-sparebank.nextcom.no/rest-api/public/v2.0/crm-system/contacts/thirdparty-add/landing-pages?id=1&friendlyUrl=j5lh4mt42g',
       false
where not exists (select 1 from public.partnere where navn = 'Soknedal Sparebank');

-- Finnes banken allerede (manuelt opprettet) uten webhook-URL, sett den (idempotent).
update public.partnere
   set webhook_url = 'https://soknedal-sparebank.nextcom.no/rest-api/public/v2.0/crm-system/contacts/thirdparty-add/landing-pages?id=1&friendlyUrl=j5lh4mt42g'
 where navn = 'Soknedal Sparebank' and coalesce(webhook_url, '') = '';
