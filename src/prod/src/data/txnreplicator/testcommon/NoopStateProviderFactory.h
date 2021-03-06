// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

#pragma once

namespace TxnReplicator
{
    namespace TestCommon
    {
        class NoopStateProviderFactory :
            public KObject<NoopStateProviderFactory>,
            public KShared<NoopStateProviderFactory>,
            public Data::StateManager::IStateProvider2Factory
        {
            K_FORCE_SHARED(NoopStateProviderFactory);
            K_SHARED_INTERFACE_IMP(IStateProvider2Factory)

        public:
            static SPtr Create(__in KAllocator& allocator);

        public:
            NTSTATUS Create(
                __in Data::StateManager::FactoryArguments const & factoryArguments,
                __out TxnReplicator::IStateProvider2::SPtr & stateProvider) noexcept override;
        };
    }
}
