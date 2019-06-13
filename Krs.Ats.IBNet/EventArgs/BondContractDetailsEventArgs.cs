using System;

namespace Krs.Ats.IBNet
{
    /// <summary>
    /// Bond Contract Details Event Arguments
    /// </summary>
    [Serializable()]
    public class BondContractDetailsEventArgs : EventArgs
    {
        private ContractDetails contractDetails;
        private int requestId;

        /// <summary>
        /// Full Constructor
        /// </summary>
        /// <param name="requestId">Request Id</param>
        /// <param name="contractDetails">This structure contains a full description of the bond contract being looked up.</param>
        public BondContractDetailsEventArgs(int requestId, ContractDetails contractDetails)
        {
            this.requestId = requestId;
            this.contractDetails = contractDetails;
        }

        /// <summary>
        /// Uninitialized Constructor for Serialization
        /// </summary>
        public BondContractDetailsEventArgs()
        {
            
        }

        /// <summary>
        /// This structure contains a full description of the bond contract being looked up.
        /// </summary>
        public ContractDetails ContractDetails
        {
            get { return contractDetails; }
			set { contractDetails = value; }
		}

        /// <summary>
        /// Request Id
        /// </summary>
        public int RequestId
        {
            get { return requestId; }
			set { requestId = value; }
		}
    }
}